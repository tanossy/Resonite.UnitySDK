using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

// "法線を焼き込む（実験的）" — Step 2 (2026-07-11, ダイダロス). Only ever invoked from
// LightmapMaterialCache.GetVariantOrOriginalInner, and only when that call site has already
// confirmed the renderer's lightmap slot has a non-null LightmapData.lightmapDir (i.e. it was
// baked with LightingSettings.directionalityMode == LightmapsMode.CombinedDirectional — see
// LightmapTestHarness.StartBakeUnity's bakeNormalDetail parameter on the calling project's side).
//
// --- Why this exists ------------------------------------------------------------------------
// Resonite/Renderite has no directional-lightmap material input at all — no "dominant direction"
// slot on any shipped material graph node, and (per LightmapMaterialCache's own class comment)
// Renderite does not support custom shaders, so a bespoke "directional lightmap" shader can't be
// shipped to Resonite either. A directional Unity bake's per-pixel normal-shading response would
// therefore normally be silently discarded — only the flat lightmapColor would ever reach
// Resonite, identical to a non-directional bake. This class approximates that discarded response
// at CONVERSION TIME (not runtime — the result is a static baked texture, exactly like the
// existing plain LightmapDecoder path) by:
//
//   1. Rendering this renderer's own interpolated *geometry* (vertex) normal into UV2 space, at
//      the same texel density as its footprint in the shared lightmap atlas (see
//      RenderUv2NormalPatch / Uv2NormalPatch.shader in this same folder). Deliberately geometry-
//      only — no tangent-space normal-map sampling. See "第2段階" note below.
//   2. Reading back that normal patch plus the matching sub-rect of the shared, already-decoded
//      color lightmap (LightmapDecoder.GetDecodedLightmap — reused as-is, not reimplemented) and
//      the RAW directional lightmap texture (LightmapData.lightmapDir — sampled with no CPU-side
//      decode step; see the note on DecodeDirectionalLightmap's real callers below).
//   3. Combining them, per texel, with UnityCG.cginc's own DecodeDirectionalLightmap() formula —
//      quoted verbatim from this Editor's actual installed CGIncludes/UnityCG.cginc (2022.3.22f1):
//
//        inline half3 DecodeDirectionalLightmap(half3 color, fixed4 dirTex, half3 normalWorld)
//        {
//            half halfLambert = dot(normalWorld, dirTex.xyz - 0.5) + 0.5;
//            return color * halfLambert / max(1e-4h, dirTex.w);
//        }
//
//      implemented here in plain C# (this only ever runs once per renderer at Editor conversion
//      time, not per-frame) rather than as a shader, since there is no rendering surface to run a
//      shader against once the source data is already sitting in CPU-side pixel arrays.
//   4. Persisting the combined result as a normal 8-bit sRGB PNG asset — same gamma-encode
//      convention LightmapDecoder.DecodeAndSave already uses for its own output (a "Color"-
//      imported texture that the GPU auto-linearizes back on sample, so the actual linear
//      multiply Resonite ends up doing matches what was computed here bit-for-bit-equivalent,
//      modulo 8-bit quantization) — so it slots into ResoniteSDK/BakedLightmapStandard's
//      _BakedLightmap property exactly like a plain decoded lightmap would.
//
// dirTex read-with-no-decode is not a guess: UnityGlobalIllumination.cginc's own
// UnityGlobalIllumination() function (this Editor's installed CGIncludes) samples
// unity_LightmapInd with a plain UNITY_SAMPLE_TEX2D_SAMPLER and passes that raw sample straight
// into DecodeDirectionalLightmap with no decode call in between — unlike unity_Lightmap (the
// color texture), which always goes through DecodeLightmap() first. This class's C#-side
// Graphics.Blit-based readback of lightmapDir therefore intentionally applies no RGBM/gamma
// transform, mirroring that real shader code path exactly.
//
// --- What this deliberately does NOT do ("第2段階", a separate follow-up task) --------------
// Only the mesh's own vertex NORMAL is used. A material's tangent-space normal map is never
// sampled — surface bump/detail that a normal map would add to the directional response is not
// reproduced by this pass, only the coarse per-triangle mesh shading is. See Uv2NormalPatch.shader
// for the exact (geometry-normal-only) rasterization this relies on.
//
// Also not attempted: dilating the combined texture into its own UV-island padding/gutter, so a
// seam is possible at UV-chart boundaries under bilinear sampling. Acceptable for this R&D pass
// per the coordinator's brief; a real-machine visual check should specifically look for this.
public static class DirectionalLightmapBaker
{
    const string Uv2NormalPatchShaderName = "ResoniteSDK/Internal/Uv2NormalPatch";

    // 2026-07-11 3rd-pass bugfix (ロキ): see RawPassthroughBlit.shader's own header comment and
    // BlitReadableAtlas below for why dirTex's Blit now goes through this material instead of
    // the argument-less Graphics.Blit(source, rt) overload this class used to call directly.
    const string RawPassthroughShaderName = "ResoniteSDK/Internal/RawPassthroughBlit";

    // Root folder for this class's own persisted output — a SIBLING of
    // LightmapVariantStorage.RootFolder's per-scene subfolders (reuses that same shared helper,
    // see GetNormalBakedLightmapInner below), so both this class's textures and
    // LightmapMaterialCache's/LightmapDecoder's assets live under one predictable, single
    // "Resonite SDK/Clear Generated Lightmap Variants" cleanup sweep with no separate menu item
    // needed for this class's own output.
    const string FilePrefix = "LMDir_";

    // Session-scoped memory front-cache, keyed by asset path — same "AssetDatabase remains the
    // actual source of truth, this dictionary only saves a repeat LoadAssetAtPath call within the
    // same session" pattern already established by LightmapDecoder._decodedByPath and
    // LightmapMaterialCache._materialByPath (see their class comments for why this is safe even
    // when empty/stale right after a domain reload).
    static readonly Dictionary<string, Texture2D> _bakedByPath = new Dictionary<string, Texture2D>();

    static bool _createdNewAssetThisCall;

    /// <summary>
    /// Returns a persisted, per-renderer combined color lightmap texture that approximates
    /// <paramref name="lightmapData"/>'s directional response for <paramref name="renderer"/> —
    /// see this class's header comment for the full algorithm. Returns null (never throws) if
    /// this renderer/mesh isn't eligible (no MeshFilter, no UV2 channel, etc.) or anything along
    /// the way fails; the caller (LightmapMaterialCache) already treats a null return as "fall
    /// back to the plain non-directional decode".
    /// </summary>
    public static Texture2D GetNormalBakedLightmap(Renderer renderer, string sceneGuid, int lightmapIndex, LightmapData lightmapData, Vector4 lightmapScaleOffset)
    {
        if (renderer == null || lightmapData == null)
            return null;

        _createdNewAssetThisCall = false;

        try
        {
            var result = GetNormalBakedLightmapInner(renderer, sceneGuid, lightmapIndex, lightmapData, lightmapScaleOffset);

            if (_createdNewAssetThisCall)
                AssetDatabase.SaveAssets();

            return result;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ResoniteSDK] DirectionalLightmapBaker: failed to build a normal-baked lightmap for " +
                $"renderer \"{renderer.name}\" (lightmap index {lightmapIndex}): {ex}. Falling back to the plain " +
                "non-directional decode for this renderer.");
            return null;
        }
    }

    static Texture2D GetNormalBakedLightmapInner(Renderer renderer, string sceneGuid, int lightmapIndex, LightmapData lightmapData, Vector4 lightmapScaleOffset)
    {
        var meshFilter = renderer.GetComponent<MeshFilter>();
        var mesh = meshFilter != null ? meshFilter.sharedMesh : null;

        if (mesh == null)
        {
            Debug.LogWarning($"[ResoniteSDK] DirectionalLightmapBaker: renderer \"{renderer.name}\" has no MeshFilter/sharedMesh; skipping normal-bake for it.");
            return null;
        }

        // Mesh.uv2 (Vector2[]) is Unity's own lightmap UV channel — confirmed via ikdasm against
        // UnityEngine.CoreModule.dll (get_uv2/set_uv2, property type Vector2[]). Same channel
        // ModelImporter.generateSecondaryUV populates (see LightmapTestHarness.
        // EnsureLightmapUVsEnabled) and the same one Uv2NormalPatch.shader's vertex input binds
        // via TEXCOORD1.
        var uv2 = mesh.uv2;
        if (uv2 == null || uv2.Length == 0)
        {
            Debug.LogWarning($"[ResoniteSDK] DirectionalLightmapBaker: mesh \"{mesh.name}\" (renderer \"{renderer.name}\") has no UV2/lightmap-UV channel; skipping normal-bake for it.");
            return null;
        }

        var colorTex = lightmapData.lightmapColor;
        var dirTex = lightmapData.lightmapDir;

        if (colorTex == null || dirTex == null)
            return null; // Caller already checked lightmapDir != null, but stay defensive here too.

        // Reuse the EXISTING, already-verified decode path/cache for the shared color atlas
        // rather than re-implementing DecodeLightmapRGBM/DecodeLightmapDoubleLDR/HDR-passthrough
        // a second time — LightmapDecoder.GetDecodedLightmap already handles all three encodings
        // and persists+caches its own result exactly like the non-directional path relies on.
        var decodedColor = LightmapDecoder.GetDecodedLightmap(sceneGuid, lightmapIndex, colorTex);
        if (decodedColor == null)
            return null; // LightmapDecoder already logged the specific reason.

        int atlasWidth = decodedColor.width;
        int atlasHeight = decodedColor.height;

        // This renderer's own tile rect within the shared atlas, from its lightmapScaleOffset
        // (xy = scale, zw = offset — the exact convention LightmapMaterialCache already uses to
        // set _BakedLightmapST for the plain path; matches Unity's own documented
        // Renderer.lightmapScaleOffset semantics: atlasUV = uv2 * scale + offset).
        //
        // Deliberately NOT clamped here (see the 2026-07-11 bugfix note above ReadAtlasRegion
        // Clamped below): tileWidth/tileHeight are this renderer's TRUE footprint size and must
        // stay exactly what the math says, uncapped, because RenderUv2NormalPatch below sizes its
        // own render target (and therefore the whole combined output texture's dimensions) off of
        // these same two values, and LightmapMaterialCache's caller relies on the result being a
        // 1:1 crop of this renderer's own uv2 range (ST=(1,1,0,0)). Only the ATLAS SAMPLE
        // coordinates need bounds-clamping when lightmapScaleOffset's zw has a small negative (or
        // xy+zw slightly overflowing) component — real, observed Unity output (e.g. a measured
        // (0.24, 0.24, -0.01, -0.01)), not a hypothetical edge case — and that clamp now happens
        // per-sample inside ReadAtlasRegionClamped instead of by sliding this whole rect's origin.
        int tileX = Mathf.RoundToInt(lightmapScaleOffset.z * atlasWidth);
        int tileY = Mathf.RoundToInt(lightmapScaleOffset.w * atlasHeight);
        int tileWidth = Mathf.Max(1, Mathf.RoundToInt(lightmapScaleOffset.x * atlasWidth));
        int tileHeight = Mathf.Max(1, Mathf.RoundToInt(lightmapScaleOffset.y * atlasHeight));

        var sceneFolder = LightmapVariantStorage.GetSceneFolder(sceneGuid);
        LightmapVariantStorage.EnsureFolder(sceneFolder);

        string rendererIdentity = GetRendererIdentity(renderer);
        string path = $"{sceneFolder}/{FilePrefix}{rendererIdentity}_{lightmapIndex}.png";

        // Cache-invalidation key: changes whenever either source texture's actual baked pixel
        // content changes (Texture2D.imageContentsHash — same field LightmapDecoder already
        // relies on for this exact purpose) OR this renderer's tile rect moves within the atlas
        // (a different UV2 packing/re-bake). Does NOT independently track "the mesh's own
        // vertex/normal data changed with no re-bake" as a separate signal — LightmapDecoder's
        // own cache has the identical limitation for its color-only hash, so this is not a new
        // gap relative to established precedent in this codebase, only an equally-scoped one.
        string sourceHash = $"{colorTex.imageContentsHash}_{dirTex.imageContentsHash}_{tileX}_{tileY}_{tileWidth}_{tileHeight}";

        if (_bakedByPath.TryGetValue(path, out var cached))
        {
            if (cached != null && IsHashCurrent(path, sourceHash))
                return cached;

            _bakedByPath.Remove(path);
        }

        var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        if (existing != null && IsHashCurrent(path, sourceHash))
        {
            _bakedByPath[path] = existing;
            return existing;
        }

        // 1) Rasterize this renderer's own geometry normals into UV2 space, at the same texel
        //    density as its atlas footprint.
        var normalPatch = RenderUv2NormalPatch(renderer, mesh, tileWidth, tileHeight);
        if (normalPatch == null)
            return null; // Already logged inside RenderUv2NormalPatch.

        // 2026-07-11 3rd-pass bugfix (ロキ): color and dir are each materialized as a FULL,
        // CPU-readable atlas texture via their own proven-orientation-correct path (see
        // LoadReadableColorAtlas / BlitReadableAtlas's own doc comments for exactly what
        // "proven" means for each) BEFORE any cropping happens, replacing this class's earlier
        // (still-wrong) from-scratch Graphics.Blit(source, rt)-based readback. ReadAtlasRegionClamped
        // below only ever does a plain Texture2D.GetPixels(x, y, w, h) against these — see its own
        // doc comment for why that specific API has no coordinate-system ambiguity left to get
        // wrong. normalPatch (above) is now ALSO materialized via this same BlitReadableAtlas path
        // (2026-07-12 bugfix — see RenderUv2NormalPatch's own doc comment), so all three of
        // normalPixels/colorPixels/dirPixels below share one single, proven orientation convention.
        var colorAtlas = LoadReadableColorAtlas(decodedColor);
        if (colorAtlas == null)
        {
            UnityEngine.Object.DestroyImmediate(normalPatch);
            return null; // Already logged inside LoadReadableColorAtlas.
        }

        var dirAtlas = BlitReadableAtlas(dirTex, atlasWidth, atlasHeight);
        if (dirAtlas == null)
        {
            UnityEngine.Object.DestroyImmediate(normalPatch);
            UnityEngine.Object.DestroyImmediate(colorAtlas);
            return null; // Already logged inside BlitReadableAtlas.
        }

        Color[] normalPixels;
        Color[] colorPixels;
        Color[] dirPixels;
        try
        {
            normalPixels = normalPatch.GetPixels();
            colorPixels = ReadAtlasRegionClamped(colorAtlas, atlasWidth, atlasHeight, tileX, tileY, tileWidth, tileHeight);
            dirPixels = ReadAtlasRegionClamped(dirAtlas, atlasWidth, atlasHeight, tileX, tileY, tileWidth, tileHeight);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(normalPatch);
            UnityEngine.Object.DestroyImmediate(colorAtlas);
            UnityEngine.Object.DestroyImmediate(dirAtlas);
        }

        // 2) Combine, per texel, via UnityCG.cginc's own DecodeDirectionalLightmap() formula —
        //    see this class's header comment for the verbatim-quoted source.
        var combined = new Color[tileWidth * tileHeight];
        for (int i = 0; i < combined.Length; i++)
        {
            var np = normalPixels[i];
            Vector3 n = new Vector3(np.r * 2f - 1f, np.g * 2f - 1f, np.b * 2f - 1f);
            float nLen = n.magnitude;
            // A texel this renderer's own UV island never covered (background/gutter, cleared to
            // (0.5,0.5,0.5) by RenderUv2NormalPatch — see its comment) decodes to a zero vector;
            // normalizing that would produce NaN, so fall back to a fixed, arbitrary-but-safe
            // "up" normal for those texels instead. This is exactly the seam/padding limitation
            // this class's header comment already calls out as unaddressed in this pass.
            Vector3 normalWorld = nLen > 1e-5f ? n / nLen : Vector3.up;

            Color dir = dirPixels[i];
            float halfLambert = Vector3.Dot(normalWorld, new Vector3(dir.r - 0.5f, dir.g - 0.5f, dir.b - 0.5f)) + 0.5f;
            float denom = Mathf.Max(1e-4f, dir.a);

            Color c = colorPixels[i];
            combined[i] = new Color(
                c.r * halfLambert / denom,
                c.g * halfLambert / denom,
                c.b * halfLambert / denom,
                1f);
        }

        var savedTex = SaveCombinedTexture(path, tileWidth, tileHeight, combined, sourceHash);
        if (savedTex != null)
        {
            _createdNewAssetThisCall = true;
            _bakedByPath[path] = savedTex;
        }

        return savedTex;
    }

    static bool IsHashCurrent(string path, string sourceHash)
    {
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        return importer != null && importer.userData == sourceHash;
    }

    /// <summary>
    /// Deterministic, process/session-stable identity string for a scene renderer, used as part
    /// of this class's persisted asset filename.
    ///
    /// 2026-07-12 bugfix (バグハンター/ロキ レビュー指摘, ダイダロス対応): this used to be a
    /// simple FNV-1a fold of the renderer's full scene hierarchy path built from GameObject
    /// NAMES only (t.name at every ancestor, joined with "/"). Two sibling GameObjects under the
    /// same parent that happen to share the same name — a routine, unremarkable mistake in
    /// prefab-heavy world authoring (duplicate + forgot to rename) — folded to the EXACT SAME
    /// hash, and therefore the exact same output PNG asset path (this identity string is used
    /// directly as part of that path, a few lines below this method's call site). The SECOND
    /// such renderer's bake silently overwrote the FIRST renderer's already-persisted LMDir_*.png
    /// with its own, unrelated content — and because the asset path doubles as this class's own
    /// cache key (_bakedByPath / IsHashCurrent), the first renderer would then read back the
    /// second renderer's directional lighting on its very next lookup, with no warning or error
    /// anywhere in the pipeline. A purely name-derived string can never distinguish this case, no
    /// matter how it's subsequently hashed — the fix has to change what's being hashed, not the
    /// hash function.
    ///
    /// Fixed by keying off UnityEditor.GlobalObjectId.GetGlobalObjectIdSlow(renderer) instead of
    /// the name-path. Confirmed as a real Editor-only API by directly grepping this project's own
    /// installed UnityEditor.dll (2022.3.22f1) for its symbols — not assumed: both the public
    /// entry point (`UnityEditor.GlobalObjectId`, `GetGlobalObjectIdSlow`) and its native-bound
    /// implementation (`GetGlobalObjectIdSlow_Injected`) are present. GlobalObjectId is Unity's
    /// own mechanism for a persistent, CONTENT-derived (scene/prefab asset GUID + the object's own
    /// local file identifier) identity for a scene object — not name-derived — so two same-named
    /// sibling GameObjects always carry two different local file identifiers and can never collide
    /// here the way the old name-path hash did. `.ToString()` is used as the actual hash input
    /// (rather than hashing the struct's fields by hand) purely so the existing FNV-1a fold below
    /// — already this codebase's own filename-hash convention, see HashScaleOffset/FloatBits in
    /// LightmapMaterialCache.cs for the equivalent precedent — can stay unchanged and keep
    /// producing the same fixed-width 8-hex-char filename component as before this fix.
    /// GlobalObjectId's own persistence guarantee (stable across Editor sessions for the same
    /// saved scene object) is a strictly STRONGER guarantee than the old name-path had, so this is
    /// a pure correctness upgrade with no new staleness risk introduced.
    /// </summary>
    static string GetRendererIdentity(Renderer renderer)
    {
        string idSource;

        try
        {
            idSource = GlobalObjectId.GetGlobalObjectIdSlow(renderer).ToString();
        }
        catch (Exception ex)
        {
            // Defensive only: GetGlobalObjectIdSlow is documented to work for any real
            // scene/asset Object and has not been observed to throw anywhere in this codebase's
            // own testing, but this method must never throw (GetNormalBakedLightmapInner has no
            // null-identity handling downstream) — fall back to the old, strictly-worse-but-still-
            // functional name-path identity rather than aborting this renderer's whole bake.
            Debug.LogWarning($"[ResoniteSDK] DirectionalLightmapBaker: GlobalObjectId.GetGlobalObjectIdSlow failed for " +
                $"renderer \"{renderer.name}\": {ex}. Falling back to a name-path-derived identity, which cannot " +
                "distinguish same-named sibling GameObjects (see this method's own doc comment for why that matters).");
            idSource = BuildNameHierarchyPath(renderer);
        }

        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in idSource)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash.ToString("x8");
        }
    }

    /// <summary>
    /// Pre-2026-07-12 identity source, kept only as GetRendererIdentity's defensive fallback if
    /// GlobalObjectId.GetGlobalObjectIdSlow itself throws — see that method's doc comment for why
    /// this alone (name-only, no true uniqueness guarantee) is no longer the primary path.
    /// </summary>
    static string BuildNameHierarchyPath(Renderer renderer)
    {
        var pathParts = new List<string>();
        var t = renderer.transform;
        while (t != null)
        {
            pathParts.Add(t.name);
            t = t.parent;
        }
        pathParts.Reverse();
        return string.Join("/", pathParts);
    }

    /// <summary>
    /// Rasterizes <paramref name="mesh"/>'s geometry (vertex) normals, transformed by
    /// <paramref name="renderer"/>'s own object-to-world matrix, into UV2 (lightmap UV) space, at
    /// <paramref name="width"/> x <paramref name="height"/> texels — see Uv2NormalPatch.shader for
    /// the actual rasterization technique. Returns a readable Texture2D (caller owns/destroys it)
    /// or null if the shader can't be found.
    ///
    /// 2026-07-12 bugfix (バグハンター指摘, ダイダロス対応): this CommandBuffer.DrawMesh pass has
    /// no camera/view-projection context at all — the render target's UV parameterization itself
    /// IS the "projection" (see this method's own DrawMesh call and Uv2NormalPatch.shader's vert()
    /// for how the mesh's UV2 is mapped straight to clip space). Uv2NormalPatch.shader used to
    /// additionally multiply its own manually-built clip-space Y by `_ProjectionParams.x` as a
    /// hand-rolled Y-flip compensation — but `_ProjectionParams` is a value Unity's own camera
    /// pipeline populates via SetupCameraProperties for whatever camera is CURRENTLY rendering;
    /// Graphics.ExecuteCommandBuffer with no camera involved never triggers that call, so reading
    /// `_ProjectionParams.x` here was reading whatever this global shader property happened to be
    /// left over from — the Scene/Game view's last-rendered camera, or 1 if nothing had rendered
    /// yet — not a value meaningfully tied to THIS pass's own render target. This is the exact
    /// same class of "silent orientation bug" this class's own header comment and
    /// LoadReadableColorAtlas/BlitReadableAtlas doc comments already describe having been found
    /// and fixed (three times) for the color/dir atlas readback on 2026-07-11 — it had simply not
    /// yet been checked for this method specifically.
    ///
    /// Fixed the same way those were: Uv2NormalPatch.shader's vert() no longer attempts any
    /// manual flip at all (see that file's own updated comment) — it renders into this method's
    /// own temporary "rawRt" RenderTexture in whatever raw orientation DrawMesh's own default
    /// clip-space write produces, and this method then routes that RT through BlitReadableAtlas
    /// exactly like
    /// BlitReadableDirAtlas already did for dirTex — i.e. through RawPassthroughBlit.shader's
    /// UnityObjectToClipPos-based vert(), which is Unity's own PROVEN (real-machine, per-renderer
    /// luminance-correlation-verified — see BlitReadableAtlas's own doc comment) per-platform
    /// Y-orientation compensation for exactly this "Blit a texture into an RT, then read that RT
    /// back" shape, instead of a second, independent, never-verified guess at the same
    /// correction. This guarantees normalPatch ends up in the IDENTICAL pixel-orientation
    /// convention as colorAtlas/dirAtlas (both of which already go through BlitReadableAtlas or an
    /// equivalent proven path), which is exactly what GetNormalBakedLightmapInner's per-index
    /// (normalPixels[i] / colorPixels[i] / dirPixels[i]) combine loop requires to be correct.
    /// </summary>
    static Texture2D RenderUv2NormalPatch(Renderer renderer, Mesh mesh, int width, int height)
    {
        var shader = Shader.Find(Uv2NormalPatchShaderName);
        if (shader == null)
        {
            Debug.LogWarning($"[ResoniteSDK] DirectionalLightmapBaker: could not find shader \"{Uv2NormalPatchShaderName}\".");
            return null;
        }

        var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        RenderTexture rawRt = null;
        CommandBuffer cmd = null;

        try
        {
            // ARGBHalf + Linear read/write: same convention LightmapDecoder's own Blit-based
            // readback already uses (see its DecodeAndSave), kept identical here for consistency
            // — HDR-range headroom isn't actually needed for a 0..1 encoded normal, but there is
            // no reason to use a different format/precision than the rest of this feature's own
            // pipeline.
            rawRt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);

            cmd = new CommandBuffer { name = "ResoniteSDK.Uv2NormalPatch" };
            cmd.SetRenderTarget(rawRt);
            // Clear to mid-gray ((0.5,0.5,0.5) decodes to the zero vector via `raw*2-1` on the
            // C# side) so texels this renderer's own UV island never actually covers (gutter/
            // background) are unambiguously "no data" rather than an arbitrary real direction —
            // see GetNormalBakedLightmapInner's own NaN-guard comment for how that's handled.
            cmd.ClearRenderTarget(true, true, new Color(0.5f, 0.5f, 0.5f, 0f));

            var matrix = renderer.transform.localToWorldMatrix;
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
                cmd.DrawMesh(mesh, matrix, material, sub);

            Graphics.ExecuteCommandBuffer(cmd);

            // See this method's own doc comment above: route the just-rendered rawRt through the
            // same proven Blit+ReadPixels helper BlitReadableAtlas already uses for dirTex, rather
            // than ReadPixels-ing directly off the DrawMesh-rendered active RT (the old,
            // _ProjectionParams.x-dependent path).
            return BlitReadableAtlas(rawRt, width, height);
        }
        finally
        {
            if (rawRt != null)
                RenderTexture.ReleaseTemporary(rawRt);
            if (cmd != null)
                cmd.Release();
            UnityEngine.Object.DestroyImmediate(material);
        }
    }

    /// <summary>
    /// 2026-07-11 3rd-pass bugfix (ロキ): loads <paramref name="decodedColor"/>'s own already-
    /// persisted PNG asset FILE BYTES directly (File.ReadAllBytes + Texture2D.LoadImage) instead
    /// of Blit-ing it through a RenderTexture at all. <paramref name="decodedColor"/> (i.e.
    /// LightmapDecoder's LMTex_{index}.png) is a real-machine-verified, CORRECTLY-ORIENTED asset —
    /// Resonite has been displaying it right-way-round in production — and LoadImage decodes the
    /// EXACT SAME PNG bytes Unity's own EncodeToPNG wrote for it, a lossless, unambiguous round
    /// trip with no separate "Blit + ReadPixels off an active render target" step in between at
    /// all (and therefore none of that step's Y-origin ambiguity — see this class's earlier,
    /// insufficient bugfix attempts still visible in this file's git history for what that
    /// ambiguity actually did: it took a real, PROVEN-correct source and, by routing it through a
    /// Graphics.Blit(source, rt) with no compensating material, silently re-oriented it before
    /// this class ever got to crop it).
    ///
    /// decodedColor's own TextureImporter almost certainly has isReadable = false (Unity's default
    /// for a freshly-imported asset, and LightmapDecoder.DecodeAndSave never explicitly sets it
    /// true) — which is exactly why the ORIGINAL code went through a Blit/RT workaround in the
    /// first place. LoadImage sidesteps that restriction entirely: it decodes raw file bytes into
    /// a brand new, always-CPU-readable Texture2D, completely independent of the asset's own
    /// import settings.
    ///
    /// The returned texture's GetPixels() values are RAW GAMMA-ENCODED bytes (LMTex_{index}.png is
    /// saved as an 8-bit sRGB "Color" PNG — see LightmapDecoder.DecodeAndSave's own final
    /// `pixels[i] = ... c.gamma` step): Texture2D.GetPixels() never applies an implicit
    /// sRGB->linear conversion on its own (that conversion normally only happens on the GPU at
    /// sample time, which is what the OLD Blit-based path was actually (correctly) relying on for
    /// this exact purpose — see this method's git-history predecessor's own doc comment). This
    /// method converts every pixel back to linear explicitly (Color.linear) before returning, so
    /// GetNormalBakedLightmapInner's DecodeDirectionalLightmap-style combine math downstream still
    /// receives the same linear values it always has — fixing the orientation bug must not
    /// silently reintroduce a gamma bug.
    /// </summary>
    static Texture2D LoadReadableColorAtlas(Texture2D decodedColor)
    {
        var assetPath = AssetDatabase.GetAssetPath(decodedColor);

        if (string.IsNullOrEmpty(assetPath))
        {
            Debug.LogWarning("[ResoniteSDK] DirectionalLightmapBaker: the decoded color lightmap has no on-disk asset path; cannot load it for the directional-bake pass.");
            return null;
        }

        byte[] bytes;

        try
        {
            bytes = File.ReadAllBytes(Path.GetFullPath(assetPath));
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ResoniteSDK] DirectionalLightmapBaker: failed to read \"{assetPath}\" for the directional-bake pass: {ex}.");
            return null;
        }

        var loaded = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);

        if (!loaded.LoadImage(bytes))
        {
            Debug.LogWarning($"[ResoniteSDK] DirectionalLightmapBaker: Texture2D.LoadImage failed for \"{assetPath}\".");
            UnityEngine.Object.DestroyImmediate(loaded);
            return null;
        }

        var pixels = loaded.GetPixels();
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = pixels[i].linear;

        loaded.SetPixels(pixels);
        loaded.Apply();

        return loaded;
    }

    /// <summary>
    /// 2026-07-11 3rd-pass bugfix (ロキ): faithfully replicates LightmapDecoder.DecodeAndSave's
    /// own Graphics.Blit(source, rt, material) + whole-image ReadPixels sequence (see that method
    /// and LightmapDecode.shader) for <paramref name="source"/>, instead of this class's own
    /// earlier from-scratch Graphics.Blit(source, rt) call (no material). Real-machine
    /// verification (per-renderer luminance correlation against LightmapDecoder's own production-
    /// proven decoded atlas) showed the material-less Blit overload does not reliably preserve
    /// the same bottom-origin pixel orientation LightmapDecoder's material-based Blit does on this
    /// project's actual D3D11 Editor target — LightmapDecode.shader's vert()
    /// (o.vertex = UnityObjectToClipPos(v.vertex)) inherits Unity's own automatic per-platform
    /// Y-orientation compensation for a Blit's fullscreen quad, which the material-less overload
    /// apparently does not get here.
    ///
    /// Uses RawPassthroughBlit.shader — the exact same vert() idiom as LightmapDecode.shader, but
    /// a pure 4-channel passthrough frag() — because LightmapDecode.shader's own frag() always
    /// forces alpha to 1, which would destroy a raw direction/normal source's own alpha/.w channel
    /// (DecodeDirectionalLightmap's formula needs dirTex's own alpha as its denominator — see this
    /// class's combine step in GetNormalBakedLightmapInner).
    ///
    /// <paramref name="source"/> is intentionally read with NO gamma/sRGB decode
    /// (RenderTextureReadWrite.Linear RT + a linear:true CPU readback texture) — because this is
    /// non-color direction/normal data, not albedo (see this class's header comment on "dirTex
    /// read-with-no-decode is not a guess"). Caller owns/destroys the returned texture.
    ///
    /// 2026-07-12 bugfix (バグハンター指摘, ダイダロス対応): generalized from
    /// "BlitReadableDirAtlas(Texture2D dirTex, ...)" to accept any <see cref="Texture"/> source —
    /// RenderUv2NormalPatch now also routes its own freshly-DrawMesh-rendered RenderTexture through
    /// this exact same proven Blit+ReadPixels sequence (a RenderTexture is itself a valid Blit
    /// source), instead of duplicating a second, independent, unverified readback with its own
    /// hand-rolled Y-flip attempt. See RenderUv2NormalPatch's own doc comment for the full story.
    /// </summary>
    static Texture2D BlitReadableAtlas(Texture source, int atlasWidth, int atlasHeight)
    {
        var shader = Shader.Find(RawPassthroughShaderName);

        if (shader == null)
        {
            Debug.LogWarning($"[ResoniteSDK] DirectionalLightmapBaker: could not find shader \"{RawPassthroughShaderName}\".");
            return null;
        }

        var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        RenderTexture rt = null;
        RenderTexture previousActive = null;
        Texture2D readable = null;

        try
        {
            rt = RenderTexture.GetTemporary(atlasWidth, atlasHeight, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            // Material-based 3-arg Blit — see this method's doc comment for why the argument-less
            // 2-arg overload this class used to call here is exactly the bug.
            Graphics.Blit(source, rt, material);

            previousActive = RenderTexture.active;
            RenderTexture.active = rt;

            readable = new Texture2D(atlasWidth, atlasHeight, TextureFormat.RGBAFloat, false, true);
            readable.ReadPixels(new Rect(0, 0, atlasWidth, atlasHeight), 0, 0);
            readable.Apply();

            return readable; // Caller owns/destroys this.
        }
        finally
        {
            if (previousActive != null || rt != null)
                RenderTexture.active = previousActive;
            if (rt != null)
                RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.DestroyImmediate(material);
        }
    }

    /// <summary>
    /// 2026-07-11 bugfix (ロキ): reads this renderer's <paramref name="tileWidth"/> x
    /// <paramref name="tileHeight"/> atlas footprint starting at (<paramref name="tileX"/>,
    /// <paramref name="tileY"/>), which — per Renderer.lightmapScaleOffset's own documented
    /// semantics (atlasUV = uv2 * scale + offset) — is derived directly from the offset
    /// component and can legitimately land partly (or, for a chart hugging the atlas edge,
    /// mostly) OUTSIDE the atlas's [0, atlasWidth) x [0, atlasHeight) bounds: real, measured
    /// Unity output has produced offsets like (0.24, 0.24, -0.01, -0.01), i.e. a small NEGATIVE
    /// zw component, not a hypothetical.
    ///
    /// The previous implementation clamped tileX/tileY straight to the atlas bounds while
    /// leaving tileWidth/tileHeight at their original (unclamped) size — which silently SLID the
    /// rect's origin over without shrinking it, so the "read" rect no longer covered this
    /// renderer's own true tile footprint at all; it covered a same-size rect shifted into
    /// whatever atlas content happened to sit at the clamped-to position instead (neighboring
    /// UV charts' pixels, inter-chart gutter/background black texels, etc.) — exactly the
    /// "wrong tile's content + black rectangle" artifact this class was reported to produce on a
    /// real Resonite upload. Meanwhile RenderUv2NormalPatch's own render target is sized purely
    /// off of tileWidth/tileHeight (unaffected by this shift) and maps this renderer's raw uv2
    /// [0,1] onto it directly, so the geometry-normal patch stayed correctly positioned the whole
    /// time — only the atlas-side color/dir crop desynced from it.
    ///
    /// The fix: keep tileWidth/tileHeight (and therefore the returned array's size, and the
    /// whole combined output texture's dimensions) always exactly the TRUE, unclamped footprint
    /// size — callers depend on that 1:1 uv2<->pixel mapping (see
    /// LightmapMaterialCache.GetVariantOrOriginalInner's ST=(1,1,0,0) comment) — and only clamp
    /// the SAMPLE coordinates actually read from the atlas, per pixel, to the nearest in-bounds
    /// atlas texel (clamp-to-edge) wherever the true rect falls outside the atlas. This keeps
    /// every output texel spatially correct relative to this renderer's own uv2, at the cost of
    /// (correctly) edge-replicating a texel or two of border in the rare case the true rect
    /// genuinely does extend past the atlas edge, instead of importing unrelated atlas content.
    ///
    /// 2026-07-11 3rd-pass bugfix (ロキ): <paramref name="atlas"/> is now always an already-fully-
    /// materialized, CPU-readable Texture2D in a PROVEN-correct orientation (see
    /// LoadReadableColorAtlas / BlitReadableAtlas, whose outputs are this method's only two
    /// callsites' inputs) — so the
    /// actual crop below is a single, direct Texture2D.GetPixels(x, y, w, h) call. No render
    /// target and no Rect-against-an-active-render-target are involved at any point in this
    /// method anymore, which is exactly the class of ambiguity earlier bugfix attempts in this
    /// class's history kept re-triggering — GetPixels(x, y, w, h) is a plain in-memory windowed
    /// read of a Texture2D's own pixel array, guaranteed by API contract to agree with a bare
    /// GetPixels() call on that same texture, with no second coordinate system left to disagree
    /// with.
    /// </summary>
    static Color[] ReadAtlasRegionClamped(Texture2D atlas, int atlasWidth, int atlasHeight, int tileX, int tileY, int tileWidth, int tileHeight)
    {
        // Sub-rect of [tileX, tileX+tileWidth) x [tileY, tileY+tileHeight) that's actually inside
        // the atlas's own bounds. Guaranteed non-empty (readX+1/readY+1 lower bounds below) even
        // for a tile rect that's entirely outside the atlas.
        int readX = Mathf.Clamp(tileX, 0, Mathf.Max(0, atlasWidth - 1));
        int readY = Mathf.Clamp(tileY, 0, Mathf.Max(0, atlasHeight - 1));
        int readRight = Mathf.Clamp(tileX + tileWidth, readX + 1, atlasWidth);
        int readTop = Mathf.Clamp(tileY + tileHeight, readY + 1, atlasHeight);
        int readWidth = readRight - readX;
        int readHeight = readTop - readY;

        var validPixels = atlas.GetPixels(readX, readY, readWidth, readHeight);

        // Fast (and overwhelmingly common) path: the true tile rect was already fully inside the
        // atlas, so the "valid" region read above already IS the full tileWidth x tileHeight
        // result with no clamping/edge-replication needed.
        if (readX == tileX && readY == tileY && readWidth == tileWidth && readHeight == tileHeight)
            return validPixels;

        // Slow path: place the valid pixels at their correct offset within the full
        // tileWidth x tileHeight destination, clamp-to-edge extending into whichever border the
        // atlas-bounds clamp above introduced, so the returned array's indexing still lines up
        // texel-for-texel with the normal patch's own tileWidth x tileHeight indexing.
        var dest = new Color[tileWidth * tileHeight];
        int offsetX = readX - tileX;
        int offsetY = readY - tileY;

        for (int y = 0; y < tileHeight; y++)
        {
            int sy = Mathf.Clamp(y - offsetY, 0, readHeight - 1);
            int destRow = y * tileWidth;
            int srcRow = sy * readWidth;

            for (int x = 0; x < tileWidth; x++)
            {
                int sx = Mathf.Clamp(x - offsetX, 0, readWidth - 1);
                dest[destRow + x] = validPixels[srcRow + sx];
            }
        }

        return dest;
    }

    /// <summary>
    /// Gamma-encodes (matching LightmapDecoder.DecodeAndSave's own convention exactly — same
    /// reasoning: the output is stored/imported as an 8-bit sRGB "Color" texture, and the GPU
    /// undoes this exact transform back to linear on sample) and persists <paramref
    /// name="combinedLinear"/> as a new PNG asset at <paramref name="path"/>.
    /// </summary>
    static Texture2D SaveCombinedTexture(string path, int width, int height, Color[] combinedLinear, string sourceHash)
    {
        var pixels = new Color[combinedLinear.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            var c = combinedLinear[i];
            var clamped = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f);
            pixels[i] = clamped.gamma;
        }

        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
        tex.SetPixels(pixels);
        tex.Apply();

        byte[] png;
        try
        {
            png = tex.EncodeToPNG();
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(tex);
        }

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
        File.WriteAllBytes(fullPath, png);

        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

        if (!(AssetImporter.GetAtPath(path) is TextureImporter importer))
        {
            Debug.LogError($"[ResoniteSDK] DirectionalLightmapBaker: failed to import combined lightmap PNG at {path}");
            return null;
        }

        importer.sRGBTexture = true;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.userData = sourceHash;
        importer.SaveAndReimport();

        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }
}
