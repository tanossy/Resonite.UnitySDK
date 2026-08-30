using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// Decodes a single entry of Unity's baked-lightmap system (LightmapSettings.lightmaps[i].
// lightmapColor) into a persisted, sRGB-encoded PNG Texture2D asset that can be handed to
// LightmapMaterialCache for use as the ResoniteSDK/BakedLightmapStandard marker material's
// _BakedLightmap property.
//
// Why this exists (Loki's review, item 3): the old implementation assigned
// LightmapSettings.lightmaps[i].lightmapColor directly as _BakedLightmap, with no decode step at
// all. Unity's baked lightmap textures are not "as-is" albedo-multiplier textures - depending on
// project/platform settings they're stored either as true HDR pixel data (BC6H / RGBAHalf /
// RGBAFloat) or as a low-dynamic-range encoded texture (RGBM, or the legacy Double-LDR scheme on
// some mobile targets) that needs UnityCG.cginc's DecodeLightmap() applied before the values mean
// anything as a 0..1+ linear light multiplier. Feeding the raw encoded texture straight into
// Renderite's SecondaryAlbedo slot would produce visibly wrong (washed out/inverted-looking)
// lighting in Resonite.
//
// Bakery note (Loki's review, item 13): this only reads LightmapSettings.lightmaps[i].lightmapColor, i.e. Bakery's
// Unity-compatible non-directional bake mode (where Bakery itself writes its result through the
// standard Unity lightmap slot). Bakery's RNM (Radiosity Normal Mapping) directional mode instead
// bakes into a separate set of custom directional textures that never touch lightmapColor, and
// those are NOT read or decoded by this pipeline - a scene baked with Bakery RNM mode will not
// get correct (or possibly any) baked lighting through this path.
public static class LightmapDecoder
{
    const string DecodeShaderName = "ResoniteSDK/Internal/LightmapDecode";

    // Mirrors UnityCG.cginc's own `#define LIGHTMAP_RGBM_SCALE 5.0` (verified against the actual
    // installed Editor version's CGIncludes/UnityCG.cginc, not assumed) - the single source of
    // truth for this constant now lives here in C# and is pushed to the decode shader as part of
    // _DecodeInstructions, instead of being duplicated as a shader-side literal (Loki's 2nd-pass
    // review, item 1).
    const float LightmapRgbmScale = 5.0f;

    // Session-scoped memory front-cache for decoded lightmap textures, keyed by asset path.
    // AssetDatabase (specifically: the persisted PNG + its TextureImporter.userData hash check in
    // GetDecodedLightmapInner) remains the actual source of truth - this dictionary only saves the
    // AssetDatabase.LoadAssetAtPath call itself on a repeat lookup within the same session; the
    // freshness check (IsHashCurrent, which calls AssetImporter.GetAtPath) still runs on every
    // lookup, hit or miss, since a cached Texture2D reference doesn't tell us whether the source
    // lightmap has since been re-baked. If a domain reload clears this dictionary, the very next
    // call simply falls through to AssetDatabase and refills it; no correctness depends on this
    // surviving a reload (Loki's 2nd-pass review, item 2 - do not reintroduce the old DontSave-object
    // cache bug this way).
    static readonly Dictionary<string, Texture2D> _decodedByPath = new Dictionary<string, Texture2D>();

    // Tracks whether GetDecodedLightmapInner actually (re-)decoded and wrote a lightmap PNG during
    // the current top-level GetDecodedLightmap call, so the wrapper below can call
    // AssetDatabase.SaveAssets() at most once per call - and only when there's actually something
    // new to persist - rather than unconditionally on every call (Loki's 2nd-pass review, item 8:
    // that would be a needless performance hit on every single already-decoded lightmap lookup).
    static bool _createdNewAssetThisCall;

    // PNG-decode range headroom. Baked lightmaps in Unity frequently carry HDR values above 1.0
    // (e.g. bright direct-light bounce, sun-facing surfaces). v1 stores the decoded result as an
    // 8-bit sRGB PNG, which can only represent the 0..1 range - any HDR headroom above 1.0 is
    // clamped away below. This multiplier is applied *before* that 0..1 clamp, as a manual
    // exposure knob a caller (or a future automated pass, once real-machine appearance is
    // verified against the source Unity bake) can use to pull an overbright lightmap's range down
    // so more of it survives the clamp instead of blowing out to solid white. Default 1.0 = no
    // adjustment. This is a static, process-wide knob rather than a per-lightmap one - v1 doesn't
    // attempt per-lightmap auto-exposure.
    //
    // 2026-08-08 (per Tanossy's feedback: "white areas should look white, brown areas should look
    // brown - we need some contrast, this is a problem"): measured against real data, this scene's
    // bake data has a linear average of roughly 0.06-0.14, which is dark, and multiplying by
    // SecondaryAlbedo alone made the whole room too dark. The option of boosting brightness via the
    // additive fill (BakedLightmapStandardConverter.AdditiveFillStrength) was rejected because it
    // flattens color contrast (raising this multiply-based value instead is the correct fix - since
    // multiplication preserves ratios, white stays white and brown stays brown while both get
    // brighter). 1.1 was the value that scene's live tuning pass converged on (an earlier trial
    // during that same pass used 3.0, which this comment used to (incorrectly) call "the verified
    // value" - it wasn't; 1.1 is).
    //
    // 2026-08-18: this is a single global constant, but the "right" boost depends on how dark a
    // given room's own bake data is - a value tuned for one scene isn't guaranteed to fit another
    // (same class of bug LightTuning.IntensityCeiling was introduced to fix on the real-time-light
    // side, just never applied here). Exposed as a slider in the Lightmap Baker panel's
    // "Send-Time Light Tuning" section (see LightmapPipelineWindow.cs) so it can be re-tuned per
    // room without a code edit; changing it here still works too (both write the same field).
    // 2026-08-31 re-calibration after the HDR export switch (per Tanossy: "Resonite is far darker
    // than Unity - adjust"). Structural reason: a Unity lightmapped surface renders as
    // albedo * lightmap (+ direct light), but in Resonite the lightmap sits in
    // SecondaryAlbedo, i.e. albedo * lightmap * (ambient + direct) - the lightmap MULTIPLIES
    // the scene lighting instead of replacing it, so with a ~0.6-mean bake the room comes out
    // several times darker. Calibrated live with the in-world LightTuning/LightmapTint driver
    // (which scales AlbedoColor, mathematically the same lever as this gain): x1 = far too
    // dark, x6 = blown out, a warm x3.1-3.3 matched Unity's Scene view closely (walls beige,
    // floor/wardrobe wood tones right, ceiling still a touch dark). 1.1 * 3.2 ~= 3.5.
    // Bake-data max was 1.6, so post-gain peaks reach ~5.6 - fine for the HDR (EXR -> BC6H)
    // path; do NOT use this value with HdrExport=false (8-bit PNG would clip most of it).
    public static float RangeScale = 3.5f;

    // 2026-08-08 (per Tanossy's feedback: "the yellow light is too strong"): LightConverter.
    // WhiteBalanceShift only pulls Point Light colors toward white, so it has no effect on the tint
    // of the baked lightmap itself (data that was originally baked with warm-colored lights).
    // Because SecondaryAlbedo is a multiply, this tinted bake data kept pulling the whole room's
    // color tone toward warm. 1.0 = color unchanged, 0.0 = fully desaturated (equivalent to
    // desaturate=true). 0.5 was the first real-machine-verified value (keeps some color character
    // while suppressing the warm cast). Same per-scene caveat and panel exposure as RangeScale
    // above.
    // 2026-08-31: back to 1.0 (keep the bake's own warm cast). The same live calibration above
    // needed a warm-biased tint (R 3.3 / G 3.1 / B 2.7) to match Unity's beige walls and warm
    // wood - i.e. the 0.6 desaturation had been removing colour the Unity reference actually
    // has. The 0.6 value dated from the pre-HDR era when the whole room read as "too yellow";
    // with the corner-seam and brightness issues fixed, the yellow was never the bake's fault.
    public static float ColorSaturationCompensation = 1.0f;

    // ResoniteLink can drop the WebSocket while importing very large decoded lightmap PNGs. Keep
    // this preview export small enough for reliable send/retry while preserving the same atlas UVs.
    //
    // 2026-07-14: disconnects recurred multiple times even at 256, so this was temporarily shrunk
    // to 64 (1/16th the pixel count of 256). However, it was then visually confirmed on real
    // hardware that "64 crushes a multi-object lightmap atlas too far and produces blocky jagged
    // artifacts" (2026-07-30, from a screenshot Tanossy reported).
    //
    // 2026-07-30 re-examination: after actually reading the ResoniteLink OSS source itself
    // (https://github.com/Yellow-Dog-Man/ResoniteLink's LinkInterface.cs), it was confirmed that
    // SendMessage has no exclusive lock and BinaryPayloadMessage's header/body two-part send is
    // non-atomic - but every call path on our SDK side (AssetConverter.Convert/
    // ProcessConversions/SendOperationBatch) synchronously blocks via `.Wait()` etc., and no path
    // was found where our own code would issue concurrent SendMessage calls. In other words, the
    // 7/14 explanation ("shrinking the payload shrinks the race window") rested on a premise
    // (concurrent calls from our own code) that doesn't actually hold, and the true causal
    // relationship remains unconfirmed (other hypotheses, such as .NET ClientWebSocket's automatic
    // KeepAlive Ping colliding with a long-running send, also remain unverified). Also, since 7/14
    // several instability factors on the Resonite side (duplicate slots, double-Destroy, etc.) have
    // been fixed at the root, so the situation may have changed since then.
    // Reverting to 256 for real-machine verification (weighing visual degradation against
    // disconnect risk; consider raising it further once stability is confirmed).
    public static int MaxPreviewTextureSize = 256;

    // 2026-08-30: HDR export, adopted from Lumos (https://github.com/ultrawidegamer/Lumos,
    // BSD-2-Clause). Reading that tool's source showed it sends the baked lightmap as raw float
    // pixels (LinkInterface.ImportTexture(ImportTexture2DRawDataHDR)) stored as BC6H_LZMA on the
    // Resonite side, so texels above 1.0 survive and the multiply-composite can *brighten* a
    // surface, not only darken it. Everything above this line (RangeScale, the 0..1 clamp, the
    // clip warning, the 256px MaxPreviewTextureSize) exists to squeeze HDR radiance into an 8-bit
    // sRGB PNG - a self-imposed loss: this SDK's own Texture2DConverter.ConvertTexture2D already
    // uploads any HDR-format Texture2D through ImportTexture2DRawDataHDR (Lumos's upload code is
    // near-verbatim that function), so the only thing standing between us and lossless HDR was
    // the file format written here.
    //
    // true  -> DecodeAndSave writes LMTex_*.exr (RGBAHalf, linear, readable, no mipmaps) with
    //          NO clamp and NO gamma encode; RangeScale/ColorSaturationCompensation still apply
    //          as plain linear multipliers; downscale limit is HdrMaxTextureSize instead of
    //          MaxPreviewTextureSize. Resonite-side StaticTexture2D settings for the result
    //          (BC6H_LZMA, Linear, MinSize 8192, ForceExactVariant) are applied by
    //          LightmapTextureSettings.cs.
    // false -> the pre-2026-08-30 8-bit sRGB PNG path, byte-for-byte unchanged.
    // Exposed as a toggle in the Lightmap Baker panel's "Baked Lightmap Exposure" section.
    // NOTE: the RangeScale=1.1 / Saturation=0.6 / LightTuning.IntensityCeiling values were all
    // tuned against the clamped PNG path - expect to re-tune once this is verified live.
    public static bool HdrExport = true;

    // Downscale limit for the HDR path. Lumos ships 1024^2 float lightmaps (16 MB raw per
    // texture) through ResoniteLink in production, which is the evidence this size is
    // transportable; keep MaxPreviewTextureSize=256 for the PNG path so that path's behavior
    // does not change.
    public static int HdrMaxTextureSize = 1024;

    /// <summary>
    /// Clears the in-memory decoded-lightmap front cache. Called from
    /// LightmapMaterialCache's "Resonite SDK/Clear Generated Lightmap Variants" menu item right
    /// before it deletes the on-disk Generated/LightmapVariants folder those cached Texture2D
    /// references point into, so nothing here can hand out a reference to an asset that no longer
    /// exists.
    /// </summary>
    public static void ClearMemoryCache() => _decodedByPath.Clear();

    /// <summary>
    /// Returns a persisted, decoded Texture2D asset for lightmap <paramref name="lightmapIndex"/>
    /// of the scene identified by <paramref name="sceneGuid"/>, decoding (or re-decoding, if the
    /// source lightmap's contents changed since the last decode) as needed. Returns null if
    /// decoding fails or the decode shader can't be found.
    ///
    /// 2026-08-08 (per Tanossy's feedback: "the sofa and the bed are completely different colors"):
    /// when <paramref name="desaturate"/>=true, this discards color information and returns a
    /// grayscale version with luminance alone duplicated across all RGB channels.
    /// BakedLightmapStandardConverter's AdditiveFillStrength (additive fill) was adding each
    /// object's actual bake-data color as-is (cool near windows / warm near lamps), which produced
    /// a different color cast per object - real Unity GI blends far more smoothly and would never
    /// diverge this much. Using this grayscale version for the additive-fill side only, while
    /// leaving the multiply side (SecondaryAlbedo) full-color as before, achieves "add brightness
    /// only".
    /// </summary>
    public static Texture2D GetDecodedLightmap(string sceneGuid, int lightmapIndex, Texture2D sourceLightmap, bool desaturate = false)
    {
        if (sourceLightmap == null)
            return null;

        _createdNewAssetThisCall = false;

        try
        {
            var result = GetDecodedLightmapInner(sceneGuid, lightmapIndex, sourceLightmap, desaturate);

            // Only persist when this specific call actually wrote/re-wrote a decoded PNG asset -
            // a cache hit (memory or AssetDatabase) has nothing new to save (Loki's 2nd-pass review,
            // item 8).
            if (_createdNewAssetThisCall)
                AssetDatabase.SaveAssets();

            return result;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ResoniteSDK] LightmapDecoder: failed to decode lightmap #{lightmapIndex} " +
                $"(\"{sourceLightmap.name}\") for scene {sceneGuid}: {ex}. Materials referencing it will fall back to no baked lightmap.");
            return null;
        }
    }

    static Texture2D GetDecodedLightmapInner(string sceneGuid, int lightmapIndex, Texture2D sourceLightmap, bool desaturate)
    {
        var folder = LightmapVariantStorage.GetSceneFolder(sceneGuid);
        LightmapVariantStorage.EnsureFolder(folder);

        // 2026-08-30: .exr for the HDR export path, .png for the legacy LDR path (see HdrExport).
        // Different extensions on purpose - flipping the toggle must never hand back a stale
        // asset of the other kind, and Texture2DConverter/LightmapTextureSettings key off the
        // "LMTex_" prefix only, so both spellings take the raw-pixel upload path.
        var ext = HdrExport ? "exr" : "png";
        var path = desaturate ? $"{folder}/LMTex_{lightmapIndex}_gray.{ext}" : $"{folder}/LMTex_{lightmapIndex}.{ext}";

        // Texture2D.imageContentsHash is a content hash of the texture's actual pixel data, so it
        // changes whenever the lightmap is re-baked (even if the file path/GUID stays the same),
        // and stays stable across Editor sessions/domain reloads for an unchanged bake. We record
        // it in the decoded PNG's own TextureImporter.userData so a later call can tell whether
        // it needs to re-decode or can just hand back the existing asset.
        var sourceHash = $"{sourceLightmap.imageContentsHash}|range:{RangeScale:0.########}|max:{MaxPreviewTextureSize}|gray:{desaturate}|sat:{ColorSaturationCompensation:0.###}|hdr:{HdrExport}|hdrmax:{HdrMaxTextureSize}|redilate:{OwnershipRedilateRadius}|underY:{UnderHeightRefillY:0.###}/{UnderHeightRefillSourceY:0.###}|shell:{ShellLateralShrink:0.###}";

        // Memory front-cache lookup first. `cached != null` uses Unity's overloaded null check,
        // so a destroyed/unloaded Texture2D (e.g. after an AssetDatabase.DeleteAsset elsewhere, or
        // Resources.UnloadUnusedAssets) is correctly treated as a miss rather than returned as a
        // dangling reference, and is evicted from the dictionary so it can't be handed out again.
        if (_decodedByPath.TryGetValue(path, out var cached))
        {
            if (cached != null && IsHashCurrent(path, sourceHash))
                return cached;

            _decodedByPath.Remove(path);
        }

        var existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

        if (existing != null && IsHashCurrent(path, sourceHash))
        {
            _decodedByPath[path] = existing;
            return existing; // Source lightmap contents are unchanged since the last decode.
        }

        var decoded = DecodeAndSave(path, sourceLightmap, sourceHash, desaturate);

        if (decoded != null)
        {
            // Only mark "there's something new to persist" when DecodeAndSave actually produced
            // an asset - an early bail-out (missing shader, failed import) wrote nothing worth an
            // extra AssetDatabase.SaveAssets() call.
            _createdNewAssetThisCall = true;
            _decodedByPath[path] = decoded;
        }

        return decoded;
    }

    /// <summary>
    /// True if the decoded PNG asset already on disk at <paramref name="path"/> was last decoded
    /// from a source lightmap with content hash <paramref name="sourceHash"/> - i.e. no re-decode
    /// is needed.
    /// </summary>
    static bool IsHashCurrent(string path, string sourceHash)
    {
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        return importer != null && importer.userData == sourceHash;
    }

    /// <summary>
    /// Chooses which decode transform to apply based on the source texture's pixel format.
    /// </summary>
    static float DetermineDecodeMode(TextureFormat format)
    {
        switch (format)
        {
            case TextureFormat.BC6H:
            case TextureFormat.RGBAHalf:
            case TextureFormat.RGBAFloat:
                // Already true HDR storage - mirrors UnityCG.cginc's DecodeLightmap()
                // "#else //defined(UNITY_LIGHTMAP_FULL_HDR) return color.rgb;" passthrough branch.
                // No transform needed.
                return 0f;

            default:
                // Not a true-HDR pixel format, so this lightmap was baked with a low-dynamic-range
                // encoding scheme instead. Unity picks between RGBM and the legacy Double-LDR
                // scheme (UNITY_LIGHTMAP_RGBM_ENCODING vs UNITY_LIGHTMAP_DLDR_ENCODING in
                // UnityCG.cginc) per active build target, and that choice isn't observable from
                // the baked texture's pixel format alone through public Editor APIs. v1 assumes
                // RGBM, matching the Editor/Standalone default and the vast majority of real-world
                // lightmap bakes. See the "remaining items dependent on real-machine verification"
                // note in this feature's design notes: confirm on a project actually using
                // Double-LDR mobile lightmaps (mode 2 in LightmapDecode.shader) before relying on
                // this for such a target.
                return 1f;
        }
    }

    /// <summary>
    /// Builds the decodeInstructions float4 LightmapDecode.shader's frag() needs for the given
    /// <paramref name="decodeMode"/> (see <see cref="DetermineDecodeMode"/>), depending on the
    /// active project color space. Pushed to the shader as the <c>_DecodeInstructions</c> material
    /// property instead of being hardcoded as shader-side literals (Loki's 2nd-pass review, item 1),
    /// and mirrors the *real* constants read directly out of this Editor install's own
    /// UnityCG.cginc (Editor/Data/CGIncludes/UnityCG.cginc) rather than assumed values:
    ///  - DecodeLightmapRGBM's x is UnityCG.cginc's own LIGHTMAP_RGBM_SCALE #define (5.0), which is
    ///    NOT color-space-dependent. Its y (2.2) is the exponent that function's own
    ///    UNITY_COLORSPACE_GAMMA-undefined (Linear) branch applies via pow(data.a, y); its
    ///    UNITY_COLORSPACE_GAMMA-defined (Gamma) branch ignores y entirely (just
    ///    decodeInstructions.x * data.a * data.rgb), so passing 2.2 unconditionally here is inert
    ///    for Gamma projects rather than wrong.
    ///  - DecodeLightmapDoubleLDR does NOT branch on UNITY_COLORSPACE_GAMMA internally the way
    ///    DecodeLightmapRGBM does - the *caller* must select x per that function's own comment:
    ///    "2.0 when gamma color space is used or pow(2.0, 2.2) = 4.59 when linear color space is
    ///    used on mobile platforms". That selection is what isGammaColorSpace drives below.
    /// </summary>
    static Vector4 DetermineDecodeInstructions(float decodeMode, bool isGammaColorSpace)
    {
        if (decodeMode < 0.5f)
            return Vector4.zero; // Mode 0 (HDR passthrough) - decodeInstructions unused by the shader.

        if (decodeMode < 1.5f)
            return new Vector4(LightmapRgbmScale, 2.2f, 0f, 0f); // Mode 1: RGBM.

        return new Vector4(isGammaColorSpace ? 2.0f : Mathf.Pow(2.0f, 2.2f), 0f, 0f, 0f); // Mode 2: Double-LDR.
    }

    static Texture2D DecodeAndSave(string path, Texture2D source, string sourceHash, bool desaturate)
    {
        var shader = Shader.Find(DecodeShaderName);

        if (shader == null)
        {
            Debug.LogWarning($"[ResoniteSDK] LightmapDecoder: could not find shader \"{DecodeShaderName}\".");
            return null;
        }

        var decodeMode = DetermineDecodeMode(source.format);

        // Loki's 2nd-pass review, item 1: which project color space (Linear vs Gamma) is active
        // changes BOTH which decodeInstructions the shader below should use AND whether the
        // linear->gamma conversion further down needs to happen at all - see the comment on that
        // conversion loop for the Gamma-space rationale.
        bool isGammaColorSpace = PlayerSettings.colorSpace == ColorSpace.Gamma;
        var decodeInstructions = DetermineDecodeInstructions(decodeMode, isGammaColorSpace);

        var blitMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };

        Color[] pixels;
        int width = source.width;
        int height = source.height;

        var previousActive = RenderTexture.active;
        var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);

        Texture2D readTex = null;

        try
        {
            blitMaterial.SetFloat("_DecodeMode", decodeMode);
            blitMaterial.SetVector("_DecodeInstructions", decodeInstructions);

            Graphics.Blit(source, rt, blitMaterial);

            RenderTexture.active = rt;

            // linear:true - we want the raw decoded values back with no implicit sRGB conversion
            // applied by the read. This is deliberately unconditional regardless of
            // isGammaColorSpace: RenderTextureReadWrite.Linear (on rt, above) and this readTex's
            // own linear:true both force the Blit + ReadPixels round trip to never apply an
            // implicit sRGB conversion either way, so what we get back here is always exactly the
            // raw shader frag() output - no hidden per-project-color-space conversion is baked in
            // by Unity anywhere in this round trip. Only the explicit branch below decides what
            // (if anything) happens to these values next.
            readTex = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
            readTex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            readTex.Apply();

            pixels = readTex.GetPixels();
        }
        finally
        {
            RenderTexture.active = previousActive;
            RenderTexture.ReleaseTemporary(rt);

            if (readTex != null)
                UnityEngine.Object.DestroyImmediate(readTex);

            UnityEngine.Object.DestroyImmediate(blitMaterial);
        }

        // 2026-08-31: re-dilate the atlas by UV2 OWNERSHIP before any downscale - see the
        // method's comment for the measured failure this fixes (bright band along the wall/
        // floor edge that the send-time gain turned pure white).
        // 2026-08-31: overwrite the sky-lit under-floor wall sliver with the visible wall's
        // colour (RefillBelowHeight), THEN re-dilate the gutter by ownership so the gutter
        // around every island carries the island's own (now corrected) edge colour instead of
        // Bakery's dilation of the bright sliver. Order matters: measured with refill alone,
        // the un-owned gutter rows kept the baked-in bright dilation (1.3 after RangeScale)
        // and the downscale still mixed them into the wall base.
        if (UnderHeightRefillY > 0f)
            RefillBelowHeight(pixels, width, height, source);

        if (OwnershipRedilateRadius > 0)
            RedilateByOwnership(pixels, width, height, source);

        int outputWidth = width;
        int outputHeight = height;
        int maxPreviewSize = Mathf.Max(1, HdrExport ? HdrMaxTextureSize : MaxPreviewTextureSize);

        if (Mathf.Max(width, height) > maxPreviewSize)
        {
            float scale = maxPreviewSize / (float)Mathf.Max(width, height);
            outputWidth = Mathf.Max(1, Mathf.RoundToInt(width * scale));
            outputHeight = Mathf.Max(1, Mathf.RoundToInt(height * scale));
            pixels = ResizeBilinear(pixels, width, height, outputWidth, outputHeight);

            Debug.Log($"[ResoniteSDK] LightmapDecoder: downscaled decoded lightmap \"{source.name}\" " +
                $"{width}x{height} -> {outputWidth}x{outputHeight} for ResoniteLink preview upload.");
        }

        // Clamp to 0..1 (after the adjustable RangeScale headroom knob - see its doc comment for
        // why HDR values above 1.0 are lost here), then - Linear color space projects only -
        // convert linear -> gamma space, since the output texture below is stored/imported as an
        // 8-bit sRGB ("Color") texture and the GPU will decode it back to linear on sample, same
        // as every other linear-space texture in the project.
        //
        // Gamma color space projects (Loki's 2nd-pass review, item 1 - double gamma baking): there is
        // no separate linear working space at all in a Gamma-space project - "color" values are
        // gamma-space all the way through Unity's own pipeline, which is exactly why
        // DecodeLightmapRGBM's UNITY_COLORSPACE_GAMMA branch (see _DecodeInstructions above, and
        // LightmapDecode.shader) skips the exponent term entirely instead of linearizing. The
        // pixels read back from the RenderTexture round trip above are already the correct,
        // final gamma-space values in that case; applying c.gamma on top of them here would
        // gamma-correct an already-gamma-space value a SECOND time, which is this exact bug.
        //
        // Loki's 3rd-pass review (item 1 follow-up): the paragraph above only holds for decodeMode
        // 1 (RGBM) and 2 (Double-LDR) - both of those decode functions themselves adjust their
        // output based on UNITY_COLORSPACE_GAMMA (see LightmapDecode.shader), which is what makes
        // skipping the extra c.gamma safe/correct for them in a Gamma project. decodeMode 0 (HDR
        // passthrough: BC6H/RGBAHalf/RGBAFloat) has no such adjustment - "decoded = raw.rgb;" in
        // the shader's mode-0 branch is *always* a raw linear radiance value, regardless of
        // project color space, because HDR lightmap textures are never gamma-encoded to begin
        // with. So mode 0 must always go through c.gamma here to become a valid 8-bit sRGB PNG,
        // even in a Gamma-space project - only skip the conversion when BOTH isGammaColorSpace AND
        // decodeMode selected the RGBM/Double-LDR path (decodeMode >= 0.5).
        bool skipGammaConversion = isGammaColorSpace && decodeMode >= 0.5f;

        // 2026-07-12 diagnostic addition (per the Bug Hunter's feedback, addressed by Daedalus): a full tonemapping
        // implementation is out of scope for this pass (see RangeScale's own doc comment - v1
        // only offers a manual exposure knob), but the silent, unwarned clamp below has been
        // measured on real bakes to actually clip real content (real, measured Unity output has
        // shown maxChannel values of 1.7-2.4 on bright highlights before this diagnostic existed).
        // Track the true (post-RangeScale, pre-clamp) max channel value and how many pixels were
        // actually affected, purely so the next bake's Console output makes that loss visible
        // instead of a bright highlight silently going flat/white with no trace anywhere.
        float maxChannelObserved = 0f;
        int clippedPixelCount = 0;

        for (int i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];

            float scaledR = c.r * RangeScale;
            float scaledG = c.g * RangeScale;
            float scaledB = c.b * RangeScale;

            float pixelMax = Mathf.Max(scaledR, Mathf.Max(scaledG, scaledB));
            if (pixelMax > maxChannelObserved)
                maxChannelObserved = pixelMax;
            if (pixelMax > 1f)
                clippedPixelCount++;

            if (desaturate)
            {
                // Rec.709 luma weights, applied in linear space before the clamp/gamma steps
                // below so the brightness-only result still goes through the exact same encode
                // path as the color version (same clamp semantics, same gamma curve).
                float luma = (0.2126f * scaledR) + (0.7152f * scaledG) + (0.0722f * scaledB);
                scaledR = scaledG = scaledB = luma;
            }
            else if (ColorSaturationCompensation < 1f)
            {
                // Partial desaturation of the "color" (multiply) variant itself - see
                // ColorSaturationCompensation's own doc comment for why this exists (the baked
                // lightmap's own warm hue, independent of LightConverter.WhiteBalanceShift which
                // only touches live Light components).
                float luma = (0.2126f * scaledR) + (0.7152f * scaledG) + (0.0722f * scaledB);
                float sat = Mathf.Clamp01(ColorSaturationCompensation);
                scaledR = Mathf.Lerp(luma, scaledR, sat);
                scaledG = Mathf.Lerp(luma, scaledG, sat);
                scaledB = Mathf.Lerp(luma, scaledB, sat);
            }

            if (HdrExport)
            {
                // 2026-08-30 HDR path: linear radiance straight through - no clamp, no gamma
                // (the EXR is imported linear, and Resonite samples it linear). Alpha forced to
                // 1 exactly like the PNG path so SecondaryAlbedo's alpha never leaks in.
                c.r = scaledR;
                c.g = scaledG;
                c.b = scaledB;
                c.a = 1f;
                pixels[i] = c;
                continue;
            }

            c.r = Mathf.Clamp01(scaledR);
            c.g = Mathf.Clamp01(scaledG);
            c.b = Mathf.Clamp01(scaledB);
            c.a = 1f;

            pixels[i] = skipGammaConversion ? c : c.gamma;
        }

        if (HdrExport)
        {
            // Informational only - nothing is clipped on this path.
            Debug.Log($"[ResoniteSDK] LightmapDecoder: HDR export of \"{source.name}\" (-> {path}) " +
                $"{outputWidth}x{outputHeight}, max channel value (after RangeScale={RangeScale:0.###}) = {maxChannelObserved:0.###}, " +
                $"{clippedPixelCount} pixel(s) above 1.0 preserved.");
        }
        else if (maxChannelObserved > 1f)
        {
            float clippedPercent = pixels.Length > 0 ? 100f * clippedPixelCount / pixels.Length : 0f;
            Debug.LogWarning($"[ResoniteSDK] LightmapDecoder: lightmap \"{source.name}\" (-> {path}) has HDR values above " +
                $"the 8-bit PNG's 0..1 range - max observed channel value (after RangeScale={RangeScale:0.###}) was " +
                $"{maxChannelObserved:0.###}, {clippedPixelCount} of {pixels.Length} pixel(s) ({clippedPercent:0.#}%) had at " +
                "least one channel clipped to 1.0 on export. Highlight detail above 1.0 has been lost in the saved PNG - " +
                "see LightmapDecoder.RangeScale's own doc comment for the manual exposure knob that can pull this back " +
                "down before it clips.");
        }

        // 2026-08-30: HDR path encodes a linear RGBAFloat texture to EXR (half-float storage, ZIP
        // compressed, lossless); LDR path is the original RGBA32 -> PNG.
        var outputTex = HdrExport
            ? new Texture2D(outputWidth, outputHeight, TextureFormat.RGBAFloat, false, true)
            : new Texture2D(outputWidth, outputHeight, TextureFormat.RGBA32, false, false);
        outputTex.SetPixels(pixels);
        outputTex.Apply();

        byte[] encoded;

        try
        {
            encoded = HdrExport
                ? outputTex.EncodeToEXR(Texture2D.EXRFlags.CompressZIP)
                : outputTex.EncodeToPNG();
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(outputTex);
        }

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
        File.WriteAllBytes(fullPath, encoded);

        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

        // Loki's 2nd-pass review, item 5: GetAtPath can legitimately return null immediately after
        // ImportAsset (e.g. the path is outside any recognized import pipeline, or the import
        // itself failed/was rejected) - a hard cast of null used to NRE right here instead of
        // failing in a way the caller could react to. Report and bail out explicitly instead;
        // GetDecodedLightmapInner's caller chain already treats a null return as "no baked
        // lightmap for this material" and falls back gracefully.
        if (!(AssetImporter.GetAtPath(path) is TextureImporter importer))
        {
            Debug.LogError($"[ResoniteSDK] Failed to import decoded lightmap PNG at {path}");
            return null;
        }

        if (HdrExport)
        {
            // Linear (not sRGB) radiance; Uncompressed so Unity imports the EXR as RGBAHalf
            // rather than BC6H (Texture2DConverter reads it back with GetPixels, which needs an
            // uncompressed format - and RGBAHalf passes its IsHDR() check, selecting the
            // ImportTexture2DRawDataHDR upload). Readable so no CopyTexture detour is needed;
            // no mipmaps (a lightmap atlas must never be sampled across island borders);
            // maxTextureSize raised so the importer never shrinks the atlas below what was
            // decoded (HdrMaxTextureSize caps it before we get here).
            importer.sRGBTexture = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.isReadable = true;
            importer.mipmapEnabled = false;
            importer.maxTextureSize = 8192;
        }
        else
        {
            importer.sRGBTexture = true;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
        }
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.userData = sourceHash;
        importer.SaveAndReimport();

        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    // 2026-08-31 (per Tanossy: "now it's blinding - a white band along the wall base"). Measured
    // on the 4096^2 bake: the wall island's own bottom rows (the 0.16 m of wall hidden under the
    // floor slab) are 0.00, but the gutter texels immediately outside that edge are ~0.5 -
    // Bakery's dilation of a bright NEIGHBOUR island runs right up to the wall's edge. The
    // visible wall starts ~3 texels above the island edge, so the 4x box downscale + bilinear
    // sampling mixes "black under-floor rows + bright gutter rows" into the wall-base texel
    // (~0.25), and the send-time gain (RangeScale 3.5 x AlbedoGain 3.3) turns that into ~3 =
    // pure white. Wider gutters don't help: whatever fills the gutter next to an island is what
    // bilinear filtering blends into its edge.
    //
    // Fix: decide gutter texels by OWNERSHIP. Every lightmapped renderer's UV2 triangles are
    // rasterised into an owner mask; then a breadth-first dilation from every owned texel fills
    // the un-owned gutter with the colour of the NEAREST owned texel - i.e. each island is
    // surrounded by its own edge colour, never a neighbour's, out to OwnershipRedilateRadius
    // texels (which must exceed the downscale factor + bilinear reach; 16 covers the 4x send
    // downscale with margin). Foreign dilation Bakery wrote into the gutter is overwritten;
    // texels actually covered by triangles are never modified. Runs on the raw decoded atlas
    // before RangeScale/downscale, on both the HDR and PNG paths.
    // 2026-08-31 outcome: alone this could not remove the band (the bright rows are owned by
    // the wall's under-floor sliver), but combined with RefillBelowHeight - which rewrites
    // those owned rows first - this pass then replaces Bakery's bright dilation in the gutter
    // with the corrected island edge colour. Runs AFTER the refill; 24 covers the 4x send
    // downscale + bilinear reach (16) plus the stray Bakery dilation measured just beyond it.
    // ON by default together with the refill (see UnderHeightRefillY).
    public static int OwnershipRedilateRadius = 24;

    static void RedilateByOwnership(Color[] pixels, int width, int height, Texture2D sourceLightmap)
    {
        int lightmapIndex = -1;
        var maps = LightmapSettings.lightmaps;
        for (int i = 0; i < maps.Length; i++)
            if (maps[i].lightmapColor == sourceLightmap) { lightmapIndex = i; break; }

        if (lightmapIndex < 0)
            return; // Not a scene lightmap (e.g. a synthetic texture in tests) - nothing to own it.

        int n = width * height;
        var owner = new int[n];      // 0 = gutter, otherwise 1-based renderer id
        int rendererId = 0;

        foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
        {
            if (r == null || r.lightmapIndex != lightmapIndex) continue;
            var mf = r.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null) continue;
            var uv2 = mesh.uv2;
            if (uv2 == null || uv2.Length != mesh.vertexCount) continue;

            rendererId++;
            var so = r.lightmapScaleOffset;

            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var tris = mesh.GetTriangles(s);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    Vector2 a = ToTexel(uv2[tris[t]], so, width, height);
                    Vector2 b = ToTexel(uv2[tris[t + 1]], so, width, height);
                    Vector2 c = ToTexel(uv2[tris[t + 2]], so, width, height);
                    RasterizeTriangle(owner, width, height, a, b, c, rendererId);
                }
            }
        }

        if (rendererId == 0)
            return;

        // BFS dilation from every owned texel into the gutter, nearest owner wins.
        var queue = new Queue<int>();
        var dist = new byte[n];
        for (int i = 0; i < n; i++)
            if (owner[i] != 0) queue.Enqueue(i);

        int radius = Mathf.Clamp(OwnershipRedilateRadius, 1, 255);
        int rewritten = 0;

        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int d = dist[i];
            if (d >= radius) continue;
            int x = i % width, y = i / width;

            for (int dy = -1; dy <= 1; dy++)
            {
                int ny = y + dy; if (ny < 0 || ny >= height) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx; if (nx < 0 || nx >= width) continue;
                    int j = ny * width + nx;
                    if (owner[j] != 0) continue;
                    owner[j] = owner[i];
                    dist[j] = (byte)(d + 1);
                    pixels[j] = pixels[i];
                    rewritten++;
                    queue.Enqueue(j);
                }
            }
        }

        Debug.Log($"[ResoniteSDK] LightmapDecoder: ownership re-dilation of \"{sourceLightmap.name}\": {rendererId} renderer(s) rasterised, " +
            $"{rewritten} gutter texel(s) rewritten within {radius} texels of their nearest island.");
    }

    // 2026-08-31 (the fix that finally kills the white band at the wall base, keeping the
    // approved full-sky look): the 0.11 m of wall hidden UNDER the floor slab (wall bottom
    // y=0.04, slab y=0.15-0.20) is sky-lit from below through the open underside of the room,
    // so its lightmap rows are ~0.5 while the visible wall just above is ~0.03. Those rows are
    // OWNED by the wall (RedilateByOwnership can't touch them), and the 4x downscale + bilinear
    // mixes them into the wall-base texel, which the send-time gain turns pure white.
    // (An alternative - BakerySkyLight.hemispherical=true - removes the cause in the bake, but
    // shifts the whole room's light distribution away from the look Tanossy approved, and the
    // view-level recalibration never converged. Not adopted.)
    //
    // RefillBelowHeight rasterises every renderer's UV2 triangles with interpolated world-space
    // height, then rewrites every texel whose surface point lies below UnderHeightRefillY with
    // the colour of the nearest texel OF THE SAME RENDERER at or above UnderHeightRefillSourceY
    // (BFS within the island, so no neighbour's colour can leak in). Runs on the raw decoded
    // atlas before the downscale. A scene with nothing below the threshold is untouched.
    // 2026-08-31: shipped OFF for one send on a misread of Tanossy's feedback - the un-refilled
    // bake immediately brought the band back along every wall base and he called it strictly
    // worse ("もっとひどくなったぞ"). ON is the approved default; what remains to improve is the
    // vertical line at wall-wall corners (the same hidden-sliver mechanism, horizontal).
    /// <summary>Texels whose surface sits below this world Y are refilled. 0 disables.</summary>
    public static float UnderHeightRefillY = 0.19f;
    /// <summary>Replacement colours are taken from texels at or above this world Y.</summary>
    public static float UnderHeightRefillSourceY = 0.30f;

    // 2026-08-31, corner follow-up ("光の帯がなくなるまでテストして"): the vertical line at
    // wall-wall corners is the same mechanism sideways. Diagnosis on the raw 4096 atlas: the
    // room-shell mesh's OUTWARD faces (normals +-x/+-z, ~700k texels at 0.5 raw, sky-lit) sit
    // in islands only ~5 texels from the inner-face islands, so nearest-wins re-dilation gives
    // the far half of that gutter the outer face's brightness and the downscale still mixes it
    // into the inner edge. Fix: for SHELL renderers (bounds contain the scene centroid and are
    // room-sized), texels whose world XZ lies outside the bounds shrunk by ShellLateralShrink
    // are treated as hidden (outer surface / corner sliver) and refilled like the under-floor
    // sliver. 0.15 m keeps the inner faces (one wall thickness ~0.31 m inside the outer AABB)
    // untouched. Ceiling / floor slabs and furniture never contain the centroid, so only the
    // wall shell is affected. Set to 0 to disable the lateral rule.
    public static float ShellLateralShrink = 0.15f;

    static void RefillBelowHeight(Color[] pixels, int width, int height, Texture2D sourceLightmap)
    {
        int lightmapIndex = -1;
        var maps = LightmapSettings.lightmaps;
        for (int i = 0; i < maps.Length; i++)
            if (maps[i].lightmapColor == sourceLightmap) { lightmapIndex = i; break; }

        if (lightmapIndex < 0)
            return;

        int n = width * height;
        var owner = new int[n];
        var worldPos = new Vector3[n];
        int rendererId = 0;

        // Scene centroid (average of lightmapped renderer bounds centres) decides which
        // renderers count as the room SHELL for the lateral rule.
        var renderers = new List<MeshRenderer>();
        var centroid = Vector3.zero;
        int centroidCount = 0;
        foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
        {
            if (r == null || r.lightmapIndex < 0) continue;
            centroid += r.bounds.center; centroidCount++;
            if (r.lightmapIndex == lightmapIndex) renderers.Add(r);
        }
        if (centroidCount > 0) centroid /= centroidCount;

        var shellByRenderer = new Dictionary<int, Bounds>(); // rendererId -> LATERALLY shrunk bounds
        foreach (var r in renderers)
        {
            var mf = r.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null) continue;
            var uv2 = mesh.uv2;
            if (uv2 == null || uv2.Length != mesh.vertexCount) continue;

            rendererId++;

            if (ShellLateralShrink > 0f && r.bounds.Contains(centroid) && r.bounds.size.magnitude > 8f)
            {
                var sb2 = r.bounds;
                sb2.Expand(new Vector3(-2f * ShellLateralShrink, 0f, -2f * ShellLateralShrink));
                shellByRenderer[rendererId] = sb2;
            }

            var so = r.lightmapScaleOffset;
            var verts = mesh.vertices;
            var transform = r.transform;

            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var tris = mesh.GetTriangles(s);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    Vector2 a = ToTexel(uv2[tris[t]], so, width, height);
                    Vector2 b = ToTexel(uv2[tris[t + 1]], so, width, height);
                    Vector2 c = ToTexel(uv2[tris[t + 2]], so, width, height);
                    Vector3 pa = transform.TransformPoint(verts[tris[t]]);
                    Vector3 pb = transform.TransformPoint(verts[tris[t + 1]]);
                    Vector3 pc = transform.TransformPoint(verts[tris[t + 2]]);
                    RasterizeTrianglePos(owner, worldPos, width, height, a, b, c, pa, pb, pc, rendererId);
                }
            }
        }

        if (rendererId == 0)
            return;

        // A texel is HIDDEN (target) if it sits below the height threshold, or - on a shell
        // renderer - laterally outside the shrunk bounds (outer surface / corner sliver).
        // Seeds are texels that are hidden by NEITHER rule and at/above the source height.
        System.Func<int, bool> hidden = i =>
        {
            if (worldPos[i].y < UnderHeightRefillY) return true;
            if (shellByRenderer.TryGetValue(owner[i], out var sb3))
            {
                var p = worldPos[i];
                if (p.x < sb3.min.x || p.x > sb3.max.x || p.z < sb3.min.z || p.z > sb3.max.z) return true;
            }
            return false;
        };

        // BFS with NO distance cap that may also cross the gutter (owner==0): entire hidden
        // islands (e.g. the shell's outer faces, which form their own UV charts with no
        // visible texel inside them) are reachable from the nearest visible island of the
        // SAME renderer across the gutter. Traversal is restricted to the gutter and the
        // seeding renderer's own texels, so no other object's colour can be picked up, and
        // only owned hidden texels are rewritten.
        var queue = new Queue<int>();
        var from = new int[n];
        var visited = new bool[n];
        for (int i = 0; i < n; i++)
        {
            from[i] = -1;
            if (owner[i] != 0 && worldPos[i].y >= UnderHeightRefillSourceY && !hidden(i))
            {
                visited[i] = true;
                from[i] = i;
                queue.Enqueue(i);
            }
        }

        int rewritten = 0;

        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int seedOwner = owner[from[i]];
            int x = i % width, y = i / width;

            for (int dy = -1; dy <= 1; dy++)
            {
                int ny = y + dy; if (ny < 0 || ny >= height) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx; if (nx < 0 || nx >= width) continue;
                    int j = ny * width + nx;
                    if (visited[j]) continue;
                    if (owner[j] != 0 && owner[j] != seedOwner) continue; // never enter another object's island
                    visited[j] = true;
                    from[j] = from[i];
                    if (owner[j] != 0 && hidden(j))
                    {
                        pixels[j] = pixels[from[j]];
                        rewritten++;
                    }
                    queue.Enqueue(j);
                }
            }
        }

        Debug.Log($"[ResoniteSDK] LightmapDecoder: hidden-texel refill of \"{sourceLightmap.name}\": " +
            $"{rewritten} texel(s) rewritten (below y={UnderHeightRefillY:0.###} or laterally outside {shellByRenderer.Count} shell renderer(s), " +
            $"sources y>={UnderHeightRefillSourceY:0.###}).");
    }

    /// <summary>RasterizeTriangle plus barycentric world-position interpolation per texel.</summary>
    static void RasterizeTrianglePos(int[] owner, Vector3[] worldPos, int width, int height,
        Vector2 a, Vector2 b, Vector2 c, Vector3 pa, Vector3 pb, Vector3 pc, int id)
    {
        int minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))) - 1);
        int maxX = Mathf.Min(width - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))) + 1);
        int minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))) - 1);
        int maxY = Mathf.Min(height - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))) + 1);
        if (minX > maxX || minY > maxY) return;

        float area = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        if (Mathf.Abs(area) < 1e-6f) return;
        float inv = 1f / area;
        const float pad = 0.5f;
        float l0 = Vector2.Distance(a, b), l1 = Vector2.Distance(b, c), l2 = Vector2.Distance(c, a);
        float tol0 = pad * l0 / Mathf.Abs(area), tol1 = pad * l1 / Mathf.Abs(area), tol2 = pad * l2 / Mathf.Abs(area);

        for (int y = minY; y <= maxY; y++)
        {
            float py = y + 0.5f;
            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f;
                float e0 = ((b.x - a.x) * (py - a.y) - (b.y - a.y) * (px - a.x)) * inv; // weight of c
                float e1 = ((c.x - b.x) * (py - b.y) - (c.y - b.y) * (px - b.x)) * inv; // weight of a
                float e2 = ((a.x - c.x) * (py - c.y) - (a.y - c.y) * (px - c.x)) * inv; // weight of b
                if (e0 >= -tol0 && e1 >= -tol1 && e2 >= -tol2)
                {
                    int i = y * width + x;
                    owner[i] = id;
                    worldPos[i] = e1 * pa + e2 * pb + e0 * pc;
                }
            }
        }
    }

    static Vector2 ToTexel(Vector2 uv, Vector4 so, int width, int height)
    {
        return new Vector2((uv.x * so.x + so.z) * width, (uv.y * so.y + so.w) * height);
    }

    /// <summary>
    /// Conservative triangle rasteriser: a texel is owned if its centre lies inside the
    /// triangle expanded by half a texel, so island edge texels count as owned.
    /// </summary>
    static void RasterizeTriangle(int[] owner, int width, int height, Vector2 a, Vector2 b, Vector2 c, int id)
    {
        int minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))) - 1);
        int maxX = Mathf.Min(width - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))) + 1);
        int minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))) - 1);
        int maxY = Mathf.Min(height - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))) + 1);
        if (minX > maxX || minY > maxY) return;

        float area = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
        if (Mathf.Abs(area) < 1e-6f) return;
        float inv = 1f / area;
        const float pad = 0.5f; // half-texel expansion, in barycentric-scaled units below

        for (int y = minY; y <= maxY; y++)
        {
            float py = y + 0.5f;
            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f;
                // Signed edge distances (in texels) - positive inside for consistent winding.
                float e0 = ((b.x - a.x) * (py - a.y) - (b.y - a.y) * (px - a.x)) * inv;
                float e1 = ((c.x - b.x) * (py - b.y) - (c.y - b.y) * (px - b.x)) * inv;
                float e2 = ((a.x - c.x) * (py - c.y) - (a.y - c.y) * (px - c.x)) * inv;
                // Convert barycentric weights back to a texel-space tolerance per edge.
                float l0 = Vector2.Distance(a, b), l1 = Vector2.Distance(b, c), l2 = Vector2.Distance(c, a);
                float tol0 = pad * l0 / Mathf.Abs(area), tol1 = pad * l1 / Mathf.Abs(area), tol2 = pad * l2 / Mathf.Abs(area);
                if (e0 >= -tol0 && e1 >= -tol1 && e2 >= -tol2)
                    owner[y * width + x] = id;
            }
        }
    }

    static Color[] ResizeBilinear(Color[] sourcePixels, int sourceWidth, int sourceHeight, int outputWidth, int outputHeight)
    {
        var resized = new Color[outputWidth * outputHeight];

        for (int y = 0; y < outputHeight; y++)
        {
            float sourceY = ((y + 0.5f) * sourceHeight / outputHeight) - 0.5f;

            for (int x = 0; x < outputWidth; x++)
            {
                float sourceX = ((x + 0.5f) * sourceWidth / outputWidth) - 0.5f;
                resized[(y * outputWidth) + x] = SampleBilinear(sourcePixels, sourceWidth, sourceHeight, sourceX, sourceY);
            }
        }

        return resized;
    }

    static Color SampleBilinear(Color[] pixels, int width, int height, float x, float y)
    {
        int x0 = Mathf.Clamp(Mathf.FloorToInt(x), 0, width - 1);
        int y0 = Mathf.Clamp(Mathf.FloorToInt(y), 0, height - 1);
        int x1 = Mathf.Clamp(x0 + 1, 0, width - 1);
        int y1 = Mathf.Clamp(y0 + 1, 0, height - 1);

        float tx = Mathf.Clamp01(x - x0);
        float ty = Mathf.Clamp01(y - y0);

        Color c00 = pixels[(y0 * width) + x0];
        Color c10 = pixels[(y0 * width) + x1];
        Color c01 = pixels[(y1 * width) + x0];
        Color c11 = pixels[(y1 * width) + x1];

        return Color.Lerp(Color.Lerp(c00, c10, tx), Color.Lerp(c01, c11, tx), ty);
    }
}
