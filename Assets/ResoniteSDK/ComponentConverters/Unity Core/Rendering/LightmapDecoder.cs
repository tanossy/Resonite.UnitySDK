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
// Why this exists (Loki review 指摘3): the old implementation assigned
// LightmapSettings.lightmaps[i].lightmapColor directly as _BakedLightmap, with no decode step at
// all. Unity's baked lightmap textures are not "as-is" albedo-multiplier textures - depending on
// project/platform settings they're stored either as true HDR pixel data (BC6H / RGBAHalf /
// RGBAFloat) or as a low-dynamic-range encoded texture (RGBM, or the legacy Double-LDR scheme on
// some mobile targets) that needs UnityCG.cginc's DecodeLightmap() applied before the values mean
// anything as a 0..1+ linear light multiplier. Feeding the raw encoded texture straight into
// Renderite's SecondaryAlbedo slot would produce visibly wrong (washed out/inverted-looking)
// lighting in Resonite.
//
// Bakery note (指摘13): this only reads LightmapSettings.lightmaps[i].lightmapColor, i.e. Bakery's
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
    // _DecodeInstructions, instead of being duplicated as a shader-side literal (Loki 2nd-pass
    // review 指摘1).
    const float LightmapRgbmScale = 5.0f;

    // Session-scoped memory front-cache for decoded lightmap textures, keyed by asset path.
    // AssetDatabase (specifically: the persisted PNG + its TextureImporter.userData hash check in
    // GetDecodedLightmapInner) remains the actual source of truth - this dictionary only saves the
    // AssetDatabase.LoadAssetAtPath call itself on a repeat lookup within the same session; the
    // freshness check (IsHashCurrent, which calls AssetImporter.GetAtPath) still runs on every
    // lookup, hit or miss, since a cached Texture2D reference doesn't tell us whether the source
    // lightmap has since been re-baked. If a domain reload clears this dictionary, the very next
    // call simply falls through to AssetDatabase and refills it; no correctness depends on this
    // surviving a reload (Loki 2nd-pass review 指摘2 - do not reintroduce the old DontSave-object
    // cache bug this way).
    static readonly Dictionary<string, Texture2D> _decodedByPath = new Dictionary<string, Texture2D>();

    // Tracks whether GetDecodedLightmapInner actually (re-)decoded and wrote a lightmap PNG during
    // the current top-level GetDecodedLightmap call, so the wrapper below can call
    // AssetDatabase.SaveAssets() at most once per call - and only when there's actually something
    // new to persist - rather than unconditionally on every call (Loki 2nd-pass review 指摘8: that
    // would be a needless performance hit on every single already-decoded lightmap lookup).
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
    public static float RangeScale = 1.0f;

    // ResoniteLink can drop the WebSocket while importing very large decoded lightmap PNGs. Keep
    // this preview export small enough for reliable send/retry while preserving the same atlas UVs.
    //
    // 2026-07-14: 256でも複数回切断が再発したため、いったん64まで縮小(256の1/16の画素数)。ただし
    // 実機で「64は複数オブジェクト分のライトマップアトラスを潰しすぎてブロック状のジャギーが出る」
    // ことが視覚的に確認された(2026-07-30、Tanossy報告のスクリーンショット)。
    //
    // 2026-07-30の再検討: ResoniteLink OSSソース本体(https://github.com/Yellow-Dog-Man/ResoniteLink
    // のLinkInterface.cs)を実際に読んだ結果、SendMessageに排他ロックが無くBinaryPayloadMessageの
    // ヘッダ/本体2送信が非アトミックであることは確認できたが、我々のSDK側の呼び出し経路
    // (AssetConverter.Convert/ProcessConversions/SendOperationBatch)は全て`.Wait()`等で同期ブロック
    // しており、自分のコードから並行SendMessage呼び出しが発生する経路は見当たらなかった。つまり
    // 「ペイロードを縮めれば競合窓が縮む」という7/14時点の説明は前提(自コードの並行呼び出し)が
    // 崩れており、正確な因果関係は未確定(.NET ClientWebSocketの自動KeepAlive Pingが長時間送信と
    // 衝突する等、他の仮説も未検証のまま)。また7/14以降にResonite側の重複スロット・二重Destroy等の
    // 不安定要因を複数根治済みなので、当時より状況が変わっている可能性がある。
    // 実機検証のため256へ戻す(見た目の劣化と切断リスクのバランスを見て、安定すればさらに引き上げ検討)。
    public static int MaxPreviewTextureSize = 256;

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
    /// </summary>
    public static Texture2D GetDecodedLightmap(string sceneGuid, int lightmapIndex, Texture2D sourceLightmap)
    {
        if (sourceLightmap == null)
            return null;

        _createdNewAssetThisCall = false;

        try
        {
            var result = GetDecodedLightmapInner(sceneGuid, lightmapIndex, sourceLightmap);

            // Only persist when this specific call actually wrote/re-wrote a decoded PNG asset -
            // a cache hit (memory or AssetDatabase) has nothing new to save (Loki 2nd-pass review
            // 指摘8).
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

    static Texture2D GetDecodedLightmapInner(string sceneGuid, int lightmapIndex, Texture2D sourceLightmap)
    {
        var folder = LightmapVariantStorage.GetSceneFolder(sceneGuid);
        LightmapVariantStorage.EnsureFolder(folder);

        var path = $"{folder}/LMTex_{lightmapIndex}.png";

        // Texture2D.imageContentsHash is a content hash of the texture's actual pixel data, so it
        // changes whenever the lightmap is re-baked (even if the file path/GUID stays the same),
        // and stays stable across Editor sessions/domain reloads for an unchanged bake. We record
        // it in the decoded PNG's own TextureImporter.userData so a later call can tell whether
        // it needs to re-decode or can just hand back the existing asset.
        var sourceHash = $"{sourceLightmap.imageContentsHash}|range:{RangeScale:0.########}|max:{MaxPreviewTextureSize}";

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

        var decoded = DecodeAndSave(path, sourceLightmap, sourceHash);

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
                // lightmap bakes. See the "残る実機検証依存項目" note in this feature's design
                // notes: confirm on a project actually using Double-LDR mobile lightmaps (mode 2
                // in LightmapDecode.shader) before relying on this for such a target.
                return 1f;
        }
    }

    /// <summary>
    /// Builds the decodeInstructions float4 LightmapDecode.shader's frag() needs for the given
    /// <paramref name="decodeMode"/> (see <see cref="DetermineDecodeMode"/>), depending on the
    /// active project color space. Pushed to the shader as the <c>_DecodeInstructions</c> material
    /// property instead of being hardcoded as shader-side literals (Loki 2nd-pass review 指摘1),
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

    static Texture2D DecodeAndSave(string path, Texture2D source, string sourceHash)
    {
        var shader = Shader.Find(DecodeShaderName);

        if (shader == null)
        {
            Debug.LogWarning($"[ResoniteSDK] LightmapDecoder: could not find shader \"{DecodeShaderName}\".");
            return null;
        }

        var decodeMode = DetermineDecodeMode(source.format);

        // Loki 2nd-pass review 指摘1: which project color space (Linear vs Gamma) is active
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

        int outputWidth = width;
        int outputHeight = height;
        int maxPreviewSize = Mathf.Max(1, MaxPreviewTextureSize);

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
        // Gamma color space projects (Loki 2nd-pass review 指摘1 - double gamma baking): there is
        // no separate linear working space at all in a Gamma-space project - "color" values are
        // gamma-space all the way through Unity's own pipeline, which is exactly why
        // DecodeLightmapRGBM's UNITY_COLORSPACE_GAMMA branch (see _DecodeInstructions above, and
        // LightmapDecode.shader) skips the exponent term entirely instead of linearizing. The
        // pixels read back from the RenderTexture round trip above are already the correct,
        // final gamma-space values in that case; applying c.gamma on top of them here would
        // gamma-correct an already-gamma-space value a SECOND time, which is this exact bug.
        //
        // Loki 3rd-pass review (指摘1 follow-up): the paragraph above only holds for decodeMode
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

        // 2026-07-12 diagnostic addition (バグハンター指摘, ダイダロス対応): a full tonemapping
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

            c.r = Mathf.Clamp01(scaledR);
            c.g = Mathf.Clamp01(scaledG);
            c.b = Mathf.Clamp01(scaledB);
            c.a = 1f;

            pixels[i] = skipGammaConversion ? c : c.gamma;
        }

        if (maxChannelObserved > 1f)
        {
            float clippedPercent = pixels.Length > 0 ? 100f * clippedPixelCount / pixels.Length : 0f;
            Debug.LogWarning($"[ResoniteSDK] LightmapDecoder: lightmap \"{source.name}\" (-> {path}) has HDR values above " +
                $"the 8-bit PNG's 0..1 range - max observed channel value (after RangeScale={RangeScale:0.###}) was " +
                $"{maxChannelObserved:0.###}, {clippedPixelCount} of {pixels.Length} pixel(s) ({clippedPercent:0.#}%) had at " +
                "least one channel clipped to 1.0 on export. Highlight detail above 1.0 has been lost in the saved PNG - " +
                "see LightmapDecoder.RangeScale's own doc comment for the manual exposure knob that can pull this back " +
                "down before it clips.");
        }

        var outputTex = new Texture2D(outputWidth, outputHeight, TextureFormat.RGBA32, false, false);
        outputTex.SetPixels(pixels);
        outputTex.Apply();

        byte[] png;

        try
        {
            png = outputTex.EncodeToPNG();
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(outputTex);
        }

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
        File.WriteAllBytes(fullPath, png);

        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);

        // Loki 2nd-pass review 指摘5: GetAtPath can legitimately return null immediately after
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

        importer.sRGBTexture = true;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.userData = sourceHash;
        importer.SaveAndReimport();

        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
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
