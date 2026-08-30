using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// LightmapPaddingPolicy.cs — pre-bake lightmap UV padding policy (2026-08-30).
//
// Why this exists (measured, not assumed — see the 2026-08-30 wiki note
// C:/urd/wiki/concepts/resonite/dev-recipes/2026-08-30_rezo_con_lumos_lightbake_repo_study.md,
// section "壁の入隅に出る「明るい線」"): a bright line along every inner wall corner of the room,
// present in Unity AND Resonite, turned out to be lightmap bleed between UV2 islands packed
// with (almost) no gutter. In the 4096^2 bake the wall's inner-face island ended at luminance
// 0.03 and the very next texel — the sunlit outer/top face island of the same mesh — was 1.26;
// bilinear filtering mixes that neighbour into the island's edge row, and the pipeline's
// 4096 -> 1024 send-time downscale (LightmapDecoder.HdrMaxTextureSize) widens the mix.
//
// Who decides the gutter: Bakery. Every bake, ftBuildGraphics.CalculateUVPadding computes
//     requiredPadding = 4 * 1024 / (sqrt(worldArea * scaleInLightmap) * texelsPerUnit)
// per mesh (clamped 1..256), stores it in ftGlobalStorage.modifiedAssets[].padding, and
// ftModelPostProcessor re-unwraps the model on import with UnwrapParam.packMargin =
// padding / 1024 (Unity's pack-margin unit is "pixels assuming the mesh fills a 1024^2
// lightmap"). The constant 4 is hardcoded, so every mesh always gets ~4 atlas texels of
// gutter — which Bakery's own dilation then fills from both sides. Editing the FBX importer
// by hand is therefore pointless: the next bake writes Bakery's value back.
//
// What this does: the same formula with a configurable target gutter (TargetGutterTexels,
// default 24 at bake resolution — 4x the send-time downscale factor plus dilation headroom),
// written through Bakery's OWN override path (the record + ftGlobalStorage.SyncModifiedAsset,
// exactly what Bakery's ftRestorePaddingMenu.cs does) and, for the Unity Progressive path,
// mirrored into ModelImporter.secondaryUVPackMargin. Raise-only: an existing larger padding
// (Bakery's or a user's) is never lowered. ftBuildGraphics.uvPaddingMax is switched on so
// Bakery's per-bake recalculation keeps the larger of the two values.
//
// Called once at the top of LightmapTestHarness.StartBake / StartBakeUnity (one line each).
// The companion LightmapSeamAudit.cs measures the result after the bake.
public static class LightmapPaddingPolicy
{
    /// <summary>Desired gutter between UV2 islands, in texels of the BAKE atlas. 0 disables the policy.</summary>
    public static int TargetGutterTexels = 24;

    /// <summary>Same clamp Bakery applies to its own value (ftBuildGraphics.CalculateUVPadding).</summary>
    const int MaxPadding = 256;

    public struct Entry
    {
        public string AssetPath;
        public string MeshName;
        public float TexelWidth;      // sqrt(area) * texelsPerUnit, Bakery's "twidth"
        public int RequiredPadding;   // our value (1024-lightmap pixel units)
        public int PreviousPadding;   // Bakery record / importer value before this run (0 = none)
        public bool Changed;
    }

    /// <summary>
    /// Computes and applies the padding for every lightmap-contributing MeshRenderer in the
    /// active scene whose mesh comes from a model file with "Generate Lightmap UVs" enabled.
    /// Returns the per-mesh log; assets whose padding was raised are reimported synchronously,
    /// so their UV2 is already regenerated when this returns.
    /// </summary>
    public static List<Entry> Apply(float texelsPerUnit, Action<string> log)
    {
        var result = new List<Entry>();
        log = log ?? (s => Debug.Log("[LightmapPaddingPolicy] " + s));

        if (TargetGutterTexels <= 0)
        {
            log("padding policy disabled (TargetGutterTexels <= 0).");
            return result;
        }

        if (texelsPerUnit <= 0f)
        {
            log($"padding policy skipped: texelsPerUnit={texelsPerUnit} is not usable.");
            return result;
        }

        // 1. Gather: mesh -> (asset path, largest twidth across instances -> smallest padding
        //    needed; Bakery keeps the LARGEST padding among instances, so do we).
        var required = new Dictionary<Mesh, Entry>();

        foreach (var renderer in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
        {
            if (renderer == null || !renderer.gameObject.activeInHierarchy)
                continue;

            var flags = GameObjectUtility.GetStaticEditorFlags(renderer.gameObject);
            if ((flags & StaticEditorFlags.ContributeGI) == 0)
                continue;

            var filter = renderer.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null)
                continue;

            var assetPath = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(assetPath))
                continue;

            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null || !importer.generateSecondaryUV)
                continue; // authored UV2 — not ours to touch (Bakery skips these too)

            float scaleInLightmap = GetScaleInLightmap(renderer);
            if (scaleInLightmap <= 0f)
                continue;

            float area = WorldArea(mesh, renderer.transform) * scaleInLightmap;
            if (area <= 0f)
                continue;

            float twidth = Mathf.Sqrt(area) * texelsPerUnit;
            int padding = Mathf.Clamp(Mathf.CeilToInt(TargetGutterTexels * (1024f / twidth)), 1, MaxPadding);

            if (required.TryGetValue(mesh, out var existing))
            {
                if (padding > existing.RequiredPadding)
                {
                    existing.RequiredPadding = padding;
                    existing.TexelWidth = twidth;
                    required[mesh] = existing;
                }
            }
            else
            {
                required[mesh] = new Entry
                {
                    AssetPath = assetPath,
                    MeshName = mesh.name,
                    TexelWidth = twidth,
                    RequiredPadding = padding,
                };
            }
        }

        if (required.Count == 0)
        {
            log("padding policy: no eligible (ContributeGI + generated-UV2 model) meshes found.");
            return result;
        }

        // 2. Apply per asset (a model file can hold several meshes).
        var dirtyAssets = new HashSet<string>();

        foreach (var pair in required)
        {
            var entry = pair.Value;
            int previous;
            bool changed = ApplyToAsset(entry.AssetPath, entry.MeshName, entry.RequiredPadding, out previous);
            entry.PreviousPadding = previous;
            entry.Changed = changed;
            result.Add(entry);

            if (changed)
                dirtyAssets.Add(entry.AssetPath);

            // Raise-only: the effective value is max(previous, required).
            int effective = Math.Max(previous, entry.RequiredPadding);
            log($"{System.IO.Path.GetFileName(entry.AssetPath)}/{entry.MeshName}: twidth={entry.TexelWidth:0} " +
                $"required {entry.RequiredPadding} for {TargetGutterTexels}-texel gutter, padding {previous} -> {effective}{(changed ? " [reimport]" : " [unchanged]")}");
        }

        // 3. Reimport: this is what actually regenerates UV2 (Bakery's ftModelPostProcessor
        //    picks the padding up from the importer's extraUserProperties record; without
        //    Bakery the ModelImporter's own secondaryUVPackMargin applies).
        if (dirtyAssets.Count > 0)
        {
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var path in dirtyAssets)
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            AssetDatabase.SaveAssets();
            log($"padding policy: reimported {dirtyAssets.Count} model asset(s) with raised UV2 padding.");
        }
        else
        {
            log("padding policy: every eligible mesh already had at least the required padding; nothing reimported.");
        }

#if BAKERY_INCLUDED
        // Bakery recalculates its own (smaller) value on every bake; with uvPaddingMax it keeps
        // the larger of (existing record, its own) instead of overwriting ours.
        ftBuildGraphics.uvPaddingMax = true;

        // 2026-08-31: second gutter that the per-mesh UV2 padding above does NOT cover - the
        // empty texels Bakery's atlas packer leaves between different OBJECTS' UV layouts
        // (BakeryProjectSettings.texelPaddingFor{Default,Xatlas}AtlasPacker, defaults 3 / 1;
        // ftBuildGraphics reads them via pstorage at pack time). Found by the post-bake
        // seam audit: after the UV2 fix the remaining findings were all "gutter=0px (next to
        // Plane001)" - furniture and wall islands packed right against the bright floor, which
        // is what the faint band along the wall/floor edge was. Same raise-only rule and the
        // same target, so both gutters survive the send-time downscale equally.
        ApplyBakeryAtlasPadding(log);
#endif

        return result;
    }

#if BAKERY_INCLUDED
    static void ApplyBakeryAtlasPadding(Action<string> log)
    {
        var settings = ftLightmaps.GetProjectSettings();
        if (settings == null)
        {
            log("padding policy: Bakery project settings not found; atlas packer padding left as is.");
            return;
        }

        int before = settings.texelPaddingForDefaultAtlasPacker;
        int beforeX = settings.texelPaddingForXatlasAtlasPacker;
        bool changed = false;

        if (settings.texelPaddingForDefaultAtlasPacker < TargetGutterTexels)
        {
            settings.texelPaddingForDefaultAtlasPacker = TargetGutterTexels;
            changed = true;
        }
        if (settings.texelPaddingForXatlasAtlasPacker < TargetGutterTexels)
        {
            settings.texelPaddingForXatlasAtlasPacker = TargetGutterTexels;
            changed = true;
        }

        if (changed)
        {
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
        }

        log($"Bakery atlas packer padding (between objects): default {before} -> {settings.texelPaddingForDefaultAtlasPacker}, " +
            $"xatlas {beforeX} -> {settings.texelPaddingForXatlasAtlasPacker} (active packer: {ftBuildGraphics.atlasPacker}){(changed ? "" : " [unchanged]")}");
    }
#endif

    /// <summary>
    /// Raise-only write of <paramref name="padding"/> for one mesh of one model asset.
    /// Returns true if anything was changed (and the asset therefore needs a reimport).
    /// </summary>
    static bool ApplyToAsset(string assetPath, string meshName, int padding, out int previous)
    {
        previous = 0;
        bool changed = false;

        var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
        if (importer == null)
            return false;

#if BAKERY_INCLUDED
        var storage = ftLightmaps.GetGlobalStorage();
        if (storage != null)
        {
            int assetIndex = storage.modifiedAssetPathList.IndexOf(assetPath);
            if (assetIndex < 0)
            {
                storage.modifiedAssetPathList.Add(assetPath);
                storage.modifiedAssets.Add(new ftGlobalStorage.AdjustedMesh
                {
                    meshName = new List<string>(),
                    padding = new List<int>(),
                    unwrapper = new List<int>(),
                });
                assetIndex = storage.modifiedAssets.Count - 1;
            }

            var record = storage.modifiedAssets[assetIndex];
            if (record.meshName == null) record.meshName = new List<string>();
            if (record.padding == null) record.padding = new List<int>();
            if (record.unwrapper == null) record.unwrapper = new List<int>();
            while (record.unwrapper.Count < record.meshName.Count) record.unwrapper.Add(0); // Bakery's own "fix legacy"

            int meshIndex = record.meshName.IndexOf(meshName);
            if (meshIndex < 0)
            {
                record.meshName.Add(meshName);
                record.padding.Add(padding);
                record.unwrapper.Add((int)ftRenderLightmap.unwrapper);
                changed = true;
            }
            else
            {
                previous = record.padding[meshIndex];
                if (previous < padding)
                {
                    record.padding[meshIndex] = padding;
                    changed = true;
                }
            }

            storage.modifiedAssets[assetIndex] = record;

            if (changed)
            {
                storage.SyncModifiedAsset(assetIndex); // -> importer.extraUserProperties "#BAKERY{json}"
                EditorUtility.SetDirty(storage);
            }
        }
#endif

        // Unity Progressive path (and a harmless mirror when Bakery is present): the importer's
        // own margin. Same unit (pixels at a 1024^2 lightmap), same raise-only rule.
        // 2026-08-31: Unity clamps secondaryUVPackMargin to 64 (Bakery's record goes to 256), so
        // compare against the clamped value - otherwise every run "raises" 64 -> 149, Unity
        // clamps it back to 64, and the model gets a pointless synchronous reimport each bake.
        // When Bakery's record already covers the mesh, the record governs the unwrap
        // (ftModelPostProcessor) and the importer margin is only a mirror.
        int importerTarget = Mathf.Min(padding, 64);
        int importerPrevious = Mathf.RoundToInt(importer.secondaryUVPackMargin); // float in Unity's API
        if (previous == 0)
            previous = importerPrevious;

        if (importerPrevious < importerTarget)
        {
            importer.secondaryUVPackMargin = importerTarget;
            if (importer.secondaryUVMarginMethod != ModelImporterSecondaryUVMarginMethod.Manual)
                importer.secondaryUVMarginMethod = ModelImporterSecondaryUVMarginMethod.Manual;
            EditorUtility.SetDirty(importer);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Bakery's own area measure (sum of |cross| over triangles in WORLD space — twice the
    /// true area, kept identical so the resulting padding is directly comparable to Bakery's).
    /// </summary>
    static float WorldArea(Mesh mesh, Transform transform)
    {
        var verts = mesh.vertices;
        var world = new Vector3[verts.Length];
        for (int i = 0; i < verts.Length; i++)
            world[i] = transform.TransformPoint(verts[i]);

        float area = 0f;
        for (int s = 0; s < mesh.subMeshCount; s++)
        {
            var tris = mesh.GetTriangles(s);
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                var a = world[tris[i]]; var b = world[tris[i + 1]]; var c = world[tris[i + 2]];
                area += Vector3.Cross(b - a, c - a).magnitude;
            }
        }
        return area;
    }

    static float GetScaleInLightmap(Renderer renderer)
    {
        var so = new SerializedObject(renderer);
        var prop = so.FindProperty("m_ScaleInLightmap");
        return prop != null ? prop.floatValue : 1f;
    }
}
