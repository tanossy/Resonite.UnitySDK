using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// Bridges Unity's baked-lightmap system (Bakery included, since it also writes its results
// through the standard renderer.lightmapIndex / lightmapScaleOffset / LightmapSettings.lightmaps
// mechanism after baking - Unity-compatible non-directional mode only, see the Bakery note on
// LightmapDecoder) into the conversion pipeline.
//
// Renderite does not support custom shaders, so we cannot ship a "lit by lightmap" shader to
// Resonite directly. Instead, when a MeshRenderer's material is Unity's Standard shader in
// Opaque mode and has a valid baked lightmap assigned, we resolve (creating on first use) a
// persisted Material *asset* using the ResoniteSDK/BakedLightmapStandard marker shader (see
// BakedLightmapStandard.shader), carrying the decoded lightmap texture + ScaleOffset as extra
// properties. BakedLightmapStandardConverter then routes that marker shader to
// PBS_MultiUV_Metallic, folding the lightmap into the SecondaryAlbedo (UV1) slot.
//
// Any material/renderer combination that doesn't meet the criteria below is returned unchanged,
// so it falls through to the regular StandardConverter (or whichever converter would otherwise
// have handled it).
//
// v1 design notes (resolves Loki's review, 13-point list):
//  - This used to be backed by a static in-memory Dictionary<CacheKey, Material> of
//    HideFlags.DontSave clones. That doesn't survive an Editor domain reload (DontSave objects
//    are destroyed on reload), which meant any AssetConversionManager converter GameObject that
//    already held a reference to a previous variant as its `Source` would end up pointing at a
//    destroyed/missing object post-reload (item 2). It also meant the cache lived only in memory,
//    so nothing on disk actually named/scoped the variants, and two different scenes converting
//    the same source material could produce colliding names/instances (items 4/5).
//
//    Instead, every variant is now a real project asset living at a deterministic path under
//    Assets/ResoniteSDK/Generated/LightmapVariants/<sceneGUID>/. Looking one up is just
//    AssetDatabase.LoadAssetAtPath - if it exists, Unity gives us back the *same* Material
//    instance it always would for that path (survives domain reloads, scene reopens, and
//    separate Editor sessions), and if it doesn't exist yet we create it once with
//    AssetDatabase.CreateAsset. There is no in-memory cache to invalidate or go stale (item 9): the
//    deterministic path *is* the cache key.
//
//  - Because a cache "hit" is now just "the asset already exists", we can no longer skip copying
//    properties on a hit - if we did, edits to the source material after the first conversion
//    would never propagate. So every property listed in GetVariantOrOriginalInner is
//    unconditionally re-read from source and written to the variant on every call; a change is
//    only detected (and EditorUtility.SetDirty called) property-by-property so we're not forcing
//    a dirty/reserialize on every single frame when nothing actually changed (item 1).
//
//  - The deterministic asset name embeds the owning scene's GUID (unsaved scenes fall back to a
//    shared "unsaved" bucket - see GetSceneGuid), so the same source material baked differently
//    in two different scenes can never collide on the same variant asset (items 4/5).
//
//  - The ScaleOffset component of the name is a bit-exact hash of the Vector4 (see HashScaleOffset
//    / FloatBits) instead of relying on float equality/GetHashCode, so two ScaleOffsets are only
//    ever treated as identical if they're bit-for-bit the same value (item 11).
//
//  - GetVariantOrOriginal never throws: eligibility checks are defensive (item 6, including an
//    explicit HasProperty("_Mode") guard), and the entire lookup/creation body is wrapped in a
//    try/catch that logs and falls back to the original material rather than aborting the whole
//    conversion (item 6).
//
// Loki 2nd-pass review notes:
//  - item 2 (perf regression): every lookup used to be a bare AssetDatabase.LoadAssetAtPath call
//    with no in-memory fronting at all. A session-scoped Dictionary<path, Material> cache (see
//    _materialByPath below) now fronts that lookup. The on-disk asset remains the actual source of
//    truth - if a domain reload clears the dictionary, the very next call just falls through to
//    LoadAssetAtPath and refills it, so correctness never depends on the dictionary surviving a
//    reload (that's exactly the old DontSave-cache bug this class was already rewritten once to
//    fix - see items 2/9 above - so this front-cache is deliberately NOT a replacement for the
//    asset-backed design, only a lookup-cost optimization on top of it). Because a cache "hit" is
//    still just "the asset already exists", GetVariantOrOriginalInner keeps re-syncing every
//    property on every call regardless of hit/miss (see the note above) - that part is unchanged
//    and is cheap (in-memory Material property writes), unlike the AssetDatabase calls this cache
//    actually saves.
//  - item 4 (orphaned assets): because variant assets are named after a scene GUID + a hash of the
//    source material + ScaleOffset, deleting/renaming a scene, or a scene being re-baked with
//    different content, can leave old variant .mat/.png assets behind under
//    Assets/ResoniteSDK/Generated/LightmapVariants/<sceneGUID>/ that nothing ever re-visits or
//    deletes. There is no automatic sweep for these (doing so safely would require knowing every
//    scene GUID that has ever existed, which isn't something we can enumerate reliably) - use the
//    "Resonite SDK/Clear Generated Lightmap Variants" menu item below to nuke the entire generated
//    folder on demand; every entry in it is fully reproducible by re-running the scene conversion.
//  - 2026-07-11 (Daedalus): GetVariantOrOriginalInner now has an additional branch, taken only
//    when the renderer's lightmap slot was baked with a directional lightmapper
//    (LightmapData.lightmapDir != null), that substitutes a per-renderer combined texture from
//    DirectionalLightmapBaker in place of the plain shared-atlas decode - see that branch's own
//    inline comment and DirectionalLightmapBaker.cs's header comment for the full design. A
//    non-directional bake (lightmapDir == null, i.e. everything before this date) takes none of
//    that code and is unaffected.
public static class LightmapMaterialCache
{
    const string BakedLightmapShaderName = "ResoniteSDK/BakedLightmapStandard";

    // Shaders whose Unity-side property block is a verified, property-for-property match with
    // Unity's built-in Standard shader for every field this file reads below (_Color, _MainTex
    // +Scale/Offset, _Metallic, _Glossiness, _MetallicGlossMap, _BumpMap, _BumpScale,
    // _OcclusionMap, _EmissionMap, _EmissionColor, _Cutoff, _Mode, and the _EMISSION keyword) —
    // so the exact same read/copy code below produces correct results for them too, without any
    // separate conversion path. Verified against source, not assumed:
    //   "Silent/Filamented" - Assets/sameR&D/Shader/Filamented_1.2/filamented-master/Filamented/
    //     Standard.shader (2026-07-12): Properties block declares every field above under the
    //     identical name, including `_pragma shader_feature _EMISSION` matching Standard's own
    //     keyword convention.
    //   "Standard_Culloff" - Assets/sameR&D/Shader/Standard_Culloff.shader (2026-07-12): same
    //     verification, same result (double-sided/cull-off variant of Standard with an otherwise
    //     identical property block).
    //   "Autodesk Interactive" - Unity's own built-in shader (2026-07-27, verified live via
    //     UnityEditor.ShaderUtil.GetPropertyName/GetPropertyCount against the actual installed
    //     shader, not assumed): declares _Color/_MainTex/_Cutoff/_Glossiness/_Metallic/
    //     _MetallicGlossMap/_BumpScale/_BumpMap/_Parallax/_ParallaxMap/_OcclusionMap/
    //     _EmissionColor/_EmissionMap/_Mode under the identical names this file reads. This is
    //     Unity's own default fallback shader assigned by the FBX importer to materials with no
    //     explicit shader baked in - very common across off-the-shelf asset-store 3D packs (this
    //     was found affecting most furniture materials in the "simple room SGB" test scene), so
    //     this is not a one-off addition.
    // Do NOT add a shader here on assumption alone - a mismatched property name would silently
    // read/write the wrong value (e.g. GetFloat on a missing property returns 0, which is
    // indistinguishable from a legitimately-set 0). Confirm against the shader's actual
    // Properties block first.
    // public (not private/internal): this file has no "Editor" folder in its path, so it compiles
    // into Assembly-CSharp, while LightmapTestHarness.cs (under Assets/Editor/) compiles into the
    // separate Assembly-CSharp-Editor — `internal` only grants same-assembly visibility, which
    // does NOT cross that boundary (confirmed via a real CS0117 here), so this must be public for
    // shader_survey's "eligible" label to read the real list instead of duplicating it.
    public static readonly HashSet<string> StandardCompatibleShaderNames = new HashSet<string>
    {
        "Standard",
        "Silent/Filamented",
        "Standard_Culloff",
        "Autodesk Interactive",
    };

    // Session-scoped memory front-cache for lightmap-variant Material assets, keyed by asset path.
    // See the class-level comment above (item 2) for the "AssetDatabase is still the source of
    // truth" invariant this relies on.
    static readonly Dictionary<string, Material> _materialByPath = new Dictionary<string, Material>();

    // Tracks whether GetVariantOrOriginalInner actually created a brand new variant Material asset
    // (via AssetDatabase.CreateAsset) during the current top-level GetVariantOrOriginal call, so
    // the wrapper below can call AssetDatabase.SaveAssets() at most once per call - and only when
    // there's a genuinely new asset that needs to exist on disk - rather than unconditionally on
    // every call, which would be a needless performance hit on every already-converted material
    // (item 8).
    static bool _createdNewAssetThisCall;

    /// <summary>
    /// Loki's 2nd-pass review, item 4 (orphaned assets, see the class-level comment): deletes the
    /// entire Assets/ResoniteSDK/Generated/LightmapVariants folder (variant materials AND decoded
    /// lightmap PNGs alike - LightmapDecoder writes its output under the same root, via
    /// LightmapVariantStorage), and clears both in-memory front caches so nothing here can hand out
    /// a reference to something that was just deleted. Every asset under that folder is fully
    /// reproducible by re-running scene conversion, so this is always safe to run.
    /// </summary>
    // 2026-08-27 (per Tanossy's feedback: "consolidate cleanup-type actions into one panel"):
    // moved out of its own standalone top-level menu item and into ResoniteSDKDebugWindow.cs
    // (menu: Resonite SDK/Open Debug Tools), alongside the other cleanup/reset buttons. This
    // method itself is unchanged - only the [MenuItem] entry point moved.
    public static void ClearGeneratedLightmapVariants()
    {
        _materialByPath.Clear();
        LightmapDecoder.ClearMemoryCache();

        if (!AssetDatabase.IsValidFolder(LightmapVariantStorage.RootFolder))
        {
            Debug.Log($"[ResoniteSDK] Nothing to clear - \"{LightmapVariantStorage.RootFolder}\" does not exist.");
            return;
        }

        if (AssetDatabase.DeleteAsset(LightmapVariantStorage.RootFolder))
            Debug.Log($"[ResoniteSDK] Deleted \"{LightmapVariantStorage.RootFolder}\". " +
                "It will be fully regenerated the next time affected scenes are converted.");
        else
            Debug.LogWarning($"[ResoniteSDK] Failed to delete \"{LightmapVariantStorage.RootFolder}\" - see the Console above for details.");
    }

    /// <summary>
    /// Returns a lightmap-carrying variant of <paramref name="source"/> if the renderer has a
    /// valid baked lightmap assigned and the material is eligible (Standard shader, Opaque mode).
    /// Otherwise returns <paramref name="source"/> unchanged.
    /// </summary>
    public static Material GetVariantOrOriginal(Renderer renderer, Material source)
    {
        if (renderer == null || source == null)
            return source;

        _createdNewAssetThisCall = false;

        try
        {
            var result = GetVariantOrOriginalInner(renderer, source);

            // Only persist when this specific call actually created a brand new variant asset -
            // property-only updates to an already-existing variant are left for Unity's normal
            // dirty-asset save path, and a non-eligible/unchanged material has nothing to save at
            // all (item 8).
            if (_createdNewAssetThisCall)
                AssetDatabase.SaveAssets();

            return result;
        }
        catch (Exception ex)
        {
            // Never let a lightmap-variant failure take down the whole material conversion -
            // fall back to the original, un-lightmapped material instead.
            Debug.LogWarning($"[ResoniteSDK] LightmapMaterialCache: failed to build a lightmap variant for material " +
                $"\"{source.name}\" on renderer \"{renderer.name}\": {ex}. Falling back to the original material without the baked lightmap.");
            return source;
        }
    }

    static Material GetVariantOrOriginalInner(Renderer renderer, Material source)
    {
        int lightmapIndex = renderer.lightmapIndex;

        if (lightmapIndex < 0 || lightmapIndex >= LightmapSettings.lightmaps.Length)
            return source;

        var lightmapData = LightmapSettings.lightmaps[lightmapIndex];

        if (lightmapData.lightmapColor == null)
            return source;

        if (source.shader == null || !StandardCompatibleShaderNames.Contains(source.shader.name))
            return source;

        // Guard the _Mode read below - some Standard-named shader variants/older serialized
        // materials may not expose it. Treat those defensively as non-eligible rather than
        // throwing out of Material.GetFloat.
        if (!source.HasProperty("_Mode"))
            return source;

        // v1 only handles Opaque (_Mode == 0). Cutout/Fade/Transparent fall through to the
        // regular conversion path.
        if (source.GetFloat("_Mode") != 0f)
            return source;

        var shader = Shader.Find(BakedLightmapShaderName);

        if (shader == null)
        {
            Debug.LogWarning($"[ResoniteSDK] LightmapMaterialCache: could not find shader \"{BakedLightmapShaderName}\". " +
                $"Falling back to the original material without the baked lightmap for \"{source.name}\".");
            return source;
        }

        var lightmapScaleOffset = renderer.lightmapScaleOffset;

        var sceneGuid = GetSceneGuid(renderer);
        var sourceIdentity = GetSourceIdentity(source);
        var stHash = HashScaleOffset(lightmapScaleOffset);

        var assetName = $"LM_{sourceIdentity}_{lightmapIndex}_{stHash}.mat";
        var folder = LightmapVariantStorage.GetSceneFolder(sceneGuid);
        var path = $"{folder}/{assetName}";

        LightmapVariantStorage.EnsureFolder(folder);

        var variant = GetCachedOrLoadMaterial(path);

        bool changed = variant == null;

        if (variant == null)
        {
            // Loki's 2nd-pass review, item 6: Material.name should be the bare asset name, not the
            // ".mat"-suffixed *filename* - Unity itself never includes the extension in an
            // asset's Object.name (compare any other .mat in the Project window), so leaving the
            // extension in here was inconsistent with every other asset in the project and would
            // show "Foo.mat" as the material's displayed name inside the Inspector/hierarchy.
            variant = new Material(shader) { name = Path.GetFileNameWithoutExtension(assetName) };
            AssetDatabase.CreateAsset(variant, path);
            _materialByPath[path] = variant;
            _createdNewAssetThisCall = true;
        }
        else if (variant.shader != shader)
        {
            // Defensive: force the marker shader back if it was ever swapped out from under us
            // (e.g. a stale asset left over from before this shader existed), so the property
            // copies below land on the properties we expect.
            variant.shader = shader;
            changed = true;
        }

        // Decode (or reuse the already-decoded, hash-checked) baked lightmap texture for this
        // scene/index pair. See LightmapDecoder for the decode + persistence logic.
        var bakedLightmapTex = LightmapDecoder.GetDecodedLightmap(sceneGuid, lightmapIndex, lightmapData.lightmapColor);

        // 2026-07-12 bugfix (per the Bug Hunter's feedback, addressed by Daedalus): this class's own header comment
        // ("returns source unchanged when eligibility conditions aren't met") promises source is returned
        // unchanged whenever this material/renderer isn't eligible - but GetDecodedLightmap can
        // itself return null (missing decode shader, failed AssetDatabase import, etc. - see its
        // own doc comment) even once every eligibility check above has already passed, and that
        // case was never checked here. Falling through with bakedLightmapTex == null would still
        // create/keep the variant Material and unconditionally SetTexture("_BakedLightmap", null)
        // on it below - producing a lightmap-marker-shaded material with NO lightmap texture
        // actually assigned, instead of honoring the documented "fall back to source" contract.
        // Bail out to the same `return source;` every other eligibility guard above already uses.
        if (bakedLightmapTex == null)
            return source;

        var bakedLightmapST = lightmapScaleOffset;

        // --- Added by Daedalus: experimental "bake in normals" path (2026-07-11) --------------------
        // Only taken when THIS lightmap slot was actually baked with a directional lightmapper
        // (lightmapData.lightmapDir != null - Unity only ever populates lightmapDir when
        // LightingSettings.directionalityMode was CombinedDirectional at bake time). Deliberately
        // gated on the baked DATA itself rather than a flag threaded in from the calling
        // project: this file has no reachable reference back to whatever harness/window
        // requested the bake (SendCurrentScene()/SceneConverter call this converter with no such
        // context), and gating on lightmapDir's actual presence means the OFF path
        // (lightmapDir == null, i.e. today's NonDirectional bake, unchanged by this whole
        // feature) is byte-for-byte identical to before this block existed - bakedLightmapTex/
        // bakedLightmapST above are only ever overwritten when the branch below both triggers
        // AND succeeds.
        //
        // Resonite/Renderite has no directional-lightmap material input (no "dominant
        // direction" slot anywhere in this SDK's material graph, and the class comment above
        // already establishes why a custom shader isn't an option) - see
        // DirectionalLightmapBaker's own header comment for the full approximation this
        // performs (per-renderer geometry-normal patch × UnityCG.cginc's own
        // DecodeDirectionalLightmap() formula, baked once into a static combined color texture
        // at Editor conversion time).
        if (lightmapData.lightmapDir != null)
        {
            var normalBaked = DirectionalLightmapBaker.GetNormalBakedLightmap(renderer, sceneGuid, lightmapIndex, lightmapData, lightmapScaleOffset);

            if (normalBaked != null)
            {
                // normalBaked is already cropped 1:1 to exactly this renderer's own lightmap
                // tile (see DirectionalLightmapBaker.GetNormalBakedLightmapInner) - it needs no
                // further scale/offset, unlike the shared whole-atlas bakedLightmapTex above.
                bakedLightmapTex = normalBaked;
                bakedLightmapST = new Vector4(1f, 1f, 0f, 0f);
            }
            // else: DirectionalLightmapBaker already Debug.LogWarning'd the specific reason
            // (missing UV2 channel, missing internal shader, etc.) and returned null - fall
            // back to the plain atlas-wide decode already computed above, exactly as if this
            // renderer's lightmap had been baked non-directional.
        }
        // ------------------------------------------------------------------------------------

        // Every property below is re-read from source and (re-)written on *every* call, even
        // when the variant asset already existed - only whether anything actually changed is
        // tracked, to decide if EditorUtility.SetDirty needs to run.
        changed |= SetColorIfChanged(variant, "_Color", source.GetColor("_Color"));
        changed |= SetTextureIfChanged(variant, "_MainTex", source.GetTexture("_MainTex"));
        changed |= SetTextureScaleIfChanged(variant, "_MainTex", source.GetTextureScale("_MainTex"));
        changed |= SetTextureOffsetIfChanged(variant, "_MainTex", source.GetTextureOffset("_MainTex"));

        changed |= SetFloatIfChanged(variant, "_Metallic", source.GetFloat("_Metallic"));
        changed |= SetFloatIfChanged(variant, "_Glossiness", source.GetFloat("_Glossiness"));
        changed |= SetTextureIfChanged(variant, "_MetallicGlossMap", source.GetTexture("_MetallicGlossMap"));

        changed |= SetTextureIfChanged(variant, "_BumpMap", source.GetTexture("_BumpMap"));
        changed |= SetFloatIfChanged(variant, "_BumpScale", source.GetFloat("_BumpScale"));

        changed |= SetTextureIfChanged(variant, "_OcclusionMap", source.GetTexture("_OcclusionMap"));

        changed |= SetTextureIfChanged(variant, "_EmissionMap", source.GetTexture("_EmissionMap"));
        changed |= SetColorIfChanged(variant, "_EmissionColor", source.GetColor("_EmissionColor"));

        changed |= SetFloatIfChanged(variant, "_Cutoff", source.GetFloat("_Cutoff"));
        changed |= SetFloatIfChanged(variant, "_Mode", source.GetFloat("_Mode"));

        // Loki's 2nd-pass review, item 7: Material.shaderKeywords (both the getter and the setter used
        // here) is obsolete API, and wholesale-copying the source material's entire keyword set is
        // dead weight anyway - BakedLightmapStandard.shader is a small, purpose-built marker
        // shader (see that file) that only actually branches on _EMISSION, so every other keyword
        // Standard might have set (_METALLICGLOSSMAP, _NORMALMAP, detail-map keywords, etc.) does
        // nothing on this shader and was only ever being copied for no effect. Sync just the one
        // keyword that matters, via the non-obsolete IsKeywordEnabled/EnableKeyword/DisableKeyword
        // API.
        bool sourceEmissionEnabled = source.IsKeywordEnabled("_EMISSION");

        if (variant.IsKeywordEnabled("_EMISSION") != sourceEmissionEnabled)
        {
            if (sourceEmissionEnabled)
                variant.EnableKeyword("_EMISSION");
            else
                variant.DisableKeyword("_EMISSION");

            changed = true;
        }

        changed |= SetTextureIfChanged(variant, "_BakedLightmap", bakedLightmapTex);
        changed |= SetVectorIfChanged(variant, "_BakedLightmapST", bakedLightmapST);

        // 2026-08-08: desaturated companion for BakedLightmapStandardConverter's additive fill
        // slot (see LightmapDecoder.GetDecodedLightmap's desaturate param doc comment). Decoded
        // from the same source lightmapData.lightmapColor as bakedLightmapTex above, independent
        // of the directional-lightmap override branch above (the desaturated fill approximates
        // ambient brightness only, so it doesn't need the per-renderer normal-baked variant).
        //
        // 2026-08-08 structural cleanup: only decode/store this when the additive fill it feeds
        // is actually enabled (BakedLightmapStandardConverter.AdditiveFillStrength > 0). At the
        // current default (0 - pure multiply, additive fill disabled), SecondaryEmissiveColor is
        // pure black regardless of what texture is bound here, so decoding (RenderTexture blit +
        // resize + PNG encode + AssetDatabase import, every single conversion) and later
        // uploading this texture to Resonite over ResoniteLink was pure waste - visually a no-op,
        // but real CPU/disk/network cost, and extra payload over a WebSocket transport already
        // known to be flaky under load. The mechanism itself is untouched - setting
        // AdditiveFillStrength back above 0 makes this decode/store path active again exactly as
        // before.
        if (BakedLightmapStandardConverter.AdditiveFillStrength > 0f)
        {
            var bakedLightmapGrayTex = LightmapDecoder.GetDecodedLightmap(sceneGuid, lightmapIndex, lightmapData.lightmapColor, desaturate: true);
            changed |= SetTextureIfChanged(variant, "_BakedLightmapGray", bakedLightmapGrayTex);
        }
        else if (variant.GetTexture("_BakedLightmapGray") != null)
        {
            // Fill was previously enabled (variant already has a gray texture bound) and has
            // since been turned back off - clear the stale reference so a future re-enable can't
            // silently pick up out-of-date pixel data before this converter runs again.
            changed |= SetTextureIfChanged(variant, "_BakedLightmapGray", null);
        }

        if (changed)
            EditorUtility.SetDirty(variant);

        return variant;
    }

    /// <summary>
    /// Resolves the GUID of the scene the given renderer lives in, so variant asset paths never
    /// collide between two scenes converting the same source material with a different bake.
    /// </summary>
    static string GetSceneGuid(Renderer renderer)
    {
        var scenePath = renderer.gameObject.scene.path;

        if (string.IsNullOrEmpty(scenePath))
        {
            // Scene has never been saved to disk (e.g. a brand-new untitled scene), so there's no
            // GUID to key variants off of. Falling back to a fixed "unsaved" bucket means lightmap
            // variants across multiple never-saved scenes could theoretically collide, but there's
            // no meaningful identity to disambiguate them with until the scene is actually saved.
            Debug.LogWarning($"[ResoniteSDK] LightmapMaterialCache: scene for renderer \"{renderer.name}\" has not been saved to disk; " +
                "lightmap variants will be stored under the shared \"unsaved\" bucket instead of a scene-specific one.");
            return "unsaved";
        }

        var guid = AssetDatabase.AssetPathToGUID(scenePath);

        return string.IsNullOrEmpty(guid) ? "unsaved" : guid;
    }

    /// <summary>
    /// Resolves a stable identity string for the source material to use in the variant's asset
    /// name.
    /// </summary>
    static string GetSourceIdentity(Material source)
    {
        if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long localId) && !string.IsNullOrEmpty(guid))
            return $"{guid}_{localId}";

        // Non-asset material (e.g. a runtime-only instance never saved to disk) -
        // TryGetGUIDAndLocalFileIdentifier can't resolve a stable identity for these, so we fall
        // back to the material's InstanceID. NOTE: this is NOT stable across Editor
        // sessions/domain reloads for what is logically "the same" material, so repeated
        // conversions of a non-asset Standard material can each mint a new variant asset here
        // (old ones become orphaned). This is a rare edge case in practice, since virtually all
        // authored materials referenced by a Unity scene's renderers are real .mat assets.
        return $"instid{source.GetInstanceID()}";
    }

    /// <summary>
    /// Bit-exact hash of the lightmap ScaleOffset Vector4, used in the variant's asset filename.
    /// We deliberately hash the raw bit patterns (not the float values via equality/GetHashCode)
    /// so two ScaleOffsets are only ever treated as "the same" when they are bit-for-bit
    /// identical - repeated bakes producing float values that differ only in the last few bits of
    /// mantissa precision must not be able to collide into a stale-looking cache hit.
    /// </summary>
    static string HashScaleOffset(Vector4 st)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + FloatBits(st.x);
            hash = hash * 31 + FloatBits(st.y);
            hash = hash * 31 + FloatBits(st.z);
            hash = hash * 31 + FloatBits(st.w);
            return hash.ToString("x8");
        }
    }

    /// <summary>
    /// Bit-exact int32 representation of a float's underlying bit pattern. Equivalent to
    /// BitConverter.SingleToInt32Bits, implemented manually via GetBytes/ToInt32 since
    /// SingleToInt32Bits isn't available on every .NET/Mono runtime version Unity might be
    /// running under.
    /// </summary>
    static int FloatBits(float value) => BitConverter.ToInt32(BitConverter.GetBytes(value), 0);

    static bool SetColorIfChanged(Material material, string property, Color value)
    {
        if (material.GetColor(property) == value)
            return false;

        material.SetColor(property, value);
        return true;
    }

    static bool SetFloatIfChanged(Material material, string property, float value)
    {
        if (material.GetFloat(property) == value)
            return false;

        material.SetFloat(property, value);
        return true;
    }

    static bool SetTextureIfChanged(Material material, string property, Texture value)
    {
        if (material.GetTexture(property) == value)
            return false;

        material.SetTexture(property, value);
        return true;
    }

    static bool SetTextureScaleIfChanged(Material material, string property, Vector2 value)
    {
        if (material.GetTextureScale(property) == value)
            return false;

        material.SetTextureScale(property, value);
        return true;
    }

    static bool SetTextureOffsetIfChanged(Material material, string property, Vector2 value)
    {
        if (material.GetTextureOffset(property) == value)
            return false;

        material.SetTextureOffset(property, value);
        return true;
    }

    static bool SetVectorIfChanged(Material material, string property, Vector4 value)
    {
        if (material.GetVector(property) == value)
            return false;

        material.SetVector(property, value);
        return true;
    }

    /// <summary>
    /// Session memory front-cache lookup for a variant Material at <paramref name="path"/>. The
    /// on-disk asset (AssetDatabase) remains the actual source of truth - see the class-level
    /// comment (item 2) for why this is safe even if the dictionary is empty/stale (e.g. right
    /// after a domain reload).
    /// </summary>
    static Material GetCachedOrLoadMaterial(string path)
    {
        if (_materialByPath.TryGetValue(path, out var cached))
        {
            // Unity's overloaded null check catches a destroyed/unloaded Material here (Unity
            // pseudo-null), not just a real null reference - a stale entry like that is evicted so
            // it can't be handed out again, and the lookup falls through to AssetDatabase below.
            if (cached != null)
                return cached;

            _materialByPath.Remove(path);
        }

        var loaded = AssetDatabase.LoadAssetAtPath<Material>(path);

        if (loaded != null)
            _materialByPath[path] = loaded;

        return loaded;
    }
}

/// <summary>
/// Shared path-building/folder-creation helpers for the on-disk lightmap variant repository, used
/// by both LightmapMaterialCache (material variants) and LightmapDecoder (decoded lightmap
/// textures), so both always agree on exactly which folder a given scene's generated assets live
/// under.
/// </summary>
internal static class LightmapVariantStorage
{
    public const string RootFolder = "Assets/ResoniteSDK/Generated/LightmapVariants";

    public static string GetSceneFolder(string sceneGuid) => $"{RootFolder}/{sceneGuid}";

    /// <summary>
    /// Creates every missing folder segment of <paramref name="folderPath"/> (which must start
    /// with "Assets"), using AssetDatabase.CreateFolder rather than raw filesystem APIs so Unity
    /// registers each folder as a real asset immediately.
    /// </summary>
    public static void EnsureFolder(string folderPath)
    {
        if (AssetDatabase.IsValidFolder(folderPath))
            return;

        var parts = folderPath.Split('/');
        var current = parts[0]; // "Assets"

        for (int i = 1; i < parts.Length; i++)
        {
            var next = $"{current}/{parts[i]}";

            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);

            current = next;
        }
    }
}
