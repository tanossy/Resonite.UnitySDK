using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// LightmapDensityPolicy.cs — per-renderer lightmap texel budget (2026-09-08).
//
// THE BUG THIS FIXES (measured on CoastCircuit_Send, all three presets):
//   A 0.6 m^2 bar stool ended up with an 11x9 texel rectangle in a 1024^2 atlas, i.e. under
//   one texel per centimetre of its chrome rim, so Resonite showed visible blocks on every
//   small prop while Unity looked smooth (Unity hides it: baked GI only feeds the diffuse
//   term, and metal props are nearly all specular). 183 of 299 lightmapped renderers were
//   under 32 texels wide. Meanwhile a single 12 m carpet was handed a rectangle 2.86x TALLER
//   THAN THE WHOLE ATLAS, starving everything else.
//
// WHY A SIMPLE FORMULA DOES NOT WORK (measured, 2026-09-08):
//   Bakery's auto-atlas is not "rect = sqrt(area * scaleInLightmap) * texelsPerUnit". It
//   accumulates per-holder area AND UV2 bounding boxes, packs, then NORMALIZES the whole tree
//   by its extent (ftBuildGraphics ~6216) and can halve a group that packed too empty. The
//   analytic prediction was off by 12x for stools and by 16x the other way for the carpet, so
//   any open-loop formula (including this file's first version) mis-allocates.
//
// WHAT THIS DOES INSTEAD — closed loop on the real thing:
//   Every bake reads what the PREVIOUS bake actually allocated (renderer.lightmapScaleOffset
//   x atlas size = the true rectangle in texels), compares it with the target for that
//   renderer's class, and corrects scaleInLightmap by that ratio (damped, clamped per step).
//   Two or three bakes converge; a scene that is already converged changes nothing. When no
//   previous bake exists the analytic estimate seeds the loop.
//
// Targets are absolute texel widths, so the presets mean something concrete. The atlas ladder
// is 512 / 1024 / 2048, matching VRChat's own PC guidance (10-20 texels/unit at 2048 max, with
// ~30 texels/unit and 4096 only on hero PC worlds) — Bakery itself caps auto-atlases at 4096:
//   Low   atlas <= 512,   props >= 24, architecture <= 160
//   Mid   atlas <= 1024,  props >= 48, architecture <= 320
//   High  atlas <= 2048,  props >= 96, architecture <= 640
// Each step doubles the width, i.e. 4x the texels. The atlas cap is also pushed into
// LightmapDecoder.HdrMaxTextureSize, because the send path downscales lightmaps to that value —
// baking 2048 and sending 1024 would throw the fix away.
public static class LightmapDensityPolicy
{
    // ---- targets (set by ApplyPreset, or directly) ----------------------------------------
    /// <summary>Minimum rectangle width (texels) a small prop should end up with.</summary>
    public static float TargetPropTexels = 64f;
    /// <summary>Maximum rectangle width (texels) a single large object may occupy.</summary>
    public static float TargetArchTexels = 512f;
    /// <summary>Auto-atlas resolution cap for this preset (also the send-time lightmap cap).</summary>
    public static int AtlasCap = 2048;
    /// <summary>Total texel budget across all renderers, keeps the atlas count sane.</summary>
    public static double MaxTotalTexels = 3.0 * 2048.0 * 2048.0;
    /// <summary>
    /// Collapse guard, as a fraction of MaxTotalTexels. Without it the first bake after a preset
    /// switch under-fills: switching High -> Low once produced a single 256x256 atlas (measured
    /// 2026-09-08) because every scaleInLightmap had just been divided down and Bakery sizes the
    /// atlas from sum(area * scale). This must stay well below 1 or it fights the closed loop —
    /// at 0.6 the High preset oscillated (x0.69, then x1.58, then x1.63 on successive bakes).
    /// Only a genuine collapse trips it, and it aims for CollapseRecoverFraction, not the budget.
    /// </summary>
    public static double MinTotalFraction = 0.25;
    /// <summary>Where the collapse guard scales back up to, as a fraction of MaxTotalTexels.</summary>
    public static double CollapseRecoverFraction = 0.5;

    // ---- loop behaviour --------------------------------------------------------------------
    /// <summary>Correction damping. 1 = full step, lower = slower but calmer convergence.</summary>
    public static float Damping = 0.75f;
    /// <summary>Largest per-bake change of scaleInLightmap, as a factor.</summary>
    public static float MaxStepFactor = 16f;
    public static float MinScaleInLightmap = 0.05f;
    public static float MaxScaleInLightmap = 512f;
    /// <summary>Within this ratio of target, a renderer is considered converged and left alone.</summary>
    public static float ConvergedTolerance = 0.15f;

    // ---- prop / architecture split ---------------------------------------------------------
    /// <summary>A mesh counts as architecture when it has at most this many connected islands…</summary>
    public static int ArchitectureIslandLimit = 16;
    /// <summary>…and covers at least this much world area (Bakery's 2x-area units).</summary>
    public static float ArchitectureMinArea = 8f;
    /// <summary>Gutter (bake texels) for architecture — the 2026-08-30 wall-corner bleed fix.</summary>
    public static int ArchitectureGutterTexels = 24;
    /// <summary>Gutter (bake texels) for props — Bakery's own default.</summary>
    public static int PropGutterTexels = 4;

    public struct Entry
    {
        public string Name;
        public string MeshName;
        public bool Architecture;
        public float BakeryArea;
        public int Islands;
        public float ActualWidth;   // texels the last bake really gave it (0 = unknown)
        public float TargetWidth;
        public float ScaleBefore;
        public float ScaleAfter;
        public bool Changed;
    }

    struct MeshInfo { public int Islands; }
    static readonly Dictionary<Mesh, MeshInfo> _meshCache = new Dictionary<Mesh, MeshInfo>();

    /// <summary>Low / Mid / High targets, derived from the preset's texels-per-unit.</summary>
    public static void ApplyPreset(float texelsPerUnit)
    {
        if (texelsPerUnit <= 10f)        { TargetPropTexels = 24f; TargetArchTexels = 160f; AtlasCap = 512;  MaxTotalTexels = 3.0 * 512 * 512; }
        else if (texelsPerUnit <= 25f)   { TargetPropTexels = 48f; TargetArchTexels = 320f; AtlasCap = 1024; MaxTotalTexels = 3.0 * 1024 * 1024; }
        else                             { TargetPropTexels = 96f; TargetArchTexels = 640f; AtlasCap = 2048; MaxTotalTexels = 3.0 * 2048 * 2048; }
    }

    public static List<Entry> Apply(float texelsPerUnit, Action<string> log)
    {
        var result = new List<Entry>();
        log = log ?? (s => Debug.Log("[LightmapDensityPolicy] " + s));

        if (texelsPerUnit <= 0f)
        {
            log($"density policy skipped: texelsPerUnit={texelsPerUnit} is not usable.");
            return result;
        }

        ApplyPreset(texelsPerUnit);
        _meshCache.Clear();

        var lightmaps = LightmapSettings.lightmaps;

        foreach (var renderer in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
        {
            if (renderer == null || !renderer.gameObject.activeInHierarchy)
                continue;
            if ((GameObjectUtility.GetStaticEditorFlags(renderer.gameObject) & StaticEditorFlags.ContributeGI) == 0)
                continue;
            var filter = renderer.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null)
                continue;

            float scaleBefore = GetScaleInLightmap(renderer);
            if (scaleBefore <= 0f)
                continue; // 0 means "not lightmapped" — the user's call, leave it

            var mesh = filter.sharedMesh;
            float area = BakeryArea(mesh, renderer.transform);
            if (area <= 0f)
                continue;

            int islands = Measure(mesh).Islands;
            bool architecture = islands <= ArchitectureIslandLimit && area >= ArchitectureMinArea;
            float target = architecture ? TargetArchTexels : TargetPropTexels;

            // What the previous bake actually gave this renderer.
            float actual = 0f;
            if (renderer.lightmapIndex >= 0 && renderer.lightmapIndex < lightmaps.Length)
            {
                var tex = lightmaps[renderer.lightmapIndex].lightmapColor;
                if (tex != null)
                {
                    var so = renderer.lightmapScaleOffset;
                    actual = Mathf.Min(Mathf.Abs(so.x) * tex.width, Mathf.Abs(so.y) * tex.height);
                }
            }

            float scaleAfter;
            if (actual > 0.01f)
            {
                // Closed loop: rectangle width scales with sqrt(scaleInLightmap), so the
                // correction on the scale is the SQUARE of the width ratio.
                float ratio = target / actual;
                if (Mathf.Abs(ratio - 1f) <= ConvergedTolerance)
                {
                    scaleAfter = scaleBefore;
                }
                else
                {
                    float step = Mathf.Pow(ratio * ratio, Damping);
                    step = Mathf.Clamp(step, 1f / MaxStepFactor, MaxStepFactor);
                    scaleAfter = scaleBefore * step;
                }
            }
            else
            {
                // Cold start: Bakery's own sizing, sqrt(area * scale) * texelsPerUnit.
                float twidth0 = Mathf.Sqrt(area) * texelsPerUnit;
                float s = target / Mathf.Max(twidth0, 1e-3f);
                scaleAfter = Mathf.Clamp(s * s, MinScaleInLightmap, MaxScaleInLightmap);
            }

            scaleAfter = Mathf.Clamp(scaleAfter, MinScaleInLightmap, MaxScaleInLightmap);

            result.Add(new Entry
            {
                Name = renderer.gameObject.name,
                MeshName = mesh.name,
                Architecture = architecture,
                BakeryArea = area,
                Islands = islands,
                ActualWidth = actual,
                TargetWidth = target,
                ScaleBefore = scaleBefore,
                ScaleAfter = scaleAfter,
            });
        }

        if (result.Count == 0)
        {
            log("density policy: no eligible (ContributeGI + mesh) renderers found.");
            return result;
        }

        // Keep the total inside the budget. The predictor is Bakery's OWN accumulation
        // (ftBuildGraphics: lmgroup.area += worldArea * scaleInLightmap, atlas side =
        // sqrt(area) * texelsPerUnit), so total atlas texels ~ sum(area*scale) * tpu^2. The
        // first version guessed from target widths instead and let a cold start ask for 36
        // atlases of 2048 (measured 2026-09-08); this predicts that before the bake.
        double total = 0;
        foreach (var e in result)
            total += (double)e.BakeryArea * e.ScaleAfter * texelsPerUnit * texelsPerUnit;
        double minTotal = MaxTotalTexels * MinTotalFraction;
        if (total > MaxTotalTexels || (total > 0 && total < minTotal))
        {
            // Same knob in both directions: scaleInLightmap multiplies area, and Bakery sizes the
            // atlas from sum(area * scale), so k here lands the atlas on the preset's size.
            double aim = total > MaxTotalTexels ? MaxTotalTexels : MaxTotalTexels * CollapseRecoverFraction;
            float k = (float)(aim / total);
            for (int i = 0; i < result.Count; i++)
            {
                var e = result[i];
                e.ScaleAfter = Mathf.Clamp(e.ScaleAfter * k, MinScaleInLightmap, MaxScaleInLightmap);
                result[i] = e;
            }
            log($"density policy: predicted {total / 1e6:0.00}M atlas texels {(total > MaxTotalTexels ? "over" : "under")} " +
                $"the {MaxTotalTexels / 1e6:0.00}M budget (floor {minTotal / 1e6:0.00}M); scales x{k:0.####}.");
        }

        // Write.
        int changed = 0, converged = 0;
        var actuals = new List<float>();
        for (int i = 0; i < result.Count; i++)
        {
            var e = result[i];
            if (e.ActualWidth > 0.01f) actuals.Add(e.ActualWidth);
            if (Mathf.Abs(e.ScaleAfter - e.ScaleBefore) > 1e-3f * Mathf.Max(1f, e.ScaleBefore))
            {
                var renderer = FindRenderer(e.Name, e.MeshName);
                if (renderer != null)
                {
                    var so = new SerializedObject(renderer);
                    var prop = so.FindProperty("m_ScaleInLightmap");
                    if (prop != null)
                    {
                        prop.floatValue = e.ScaleAfter;
                        so.ApplyModifiedPropertiesWithoutUndo();
                        e.Changed = true;
                        changed++;
                    }
                }
            }
            else converged++;
            result[i] = e;
        }

#if BAKERY_INCLUDED
        // Round auto-atlas resolutions UP: with ClosestPowerOfTwo the atlas can be 1.4x too
        // small, the packer overflows and normalization then shrinks every rectangle in it.
        ftBuildGraphics.atlasCountPriority = true;
        ftBuildGraphics.maxAutoResolution = AtlasCap;
#endif
        // The send path must not undo the bake: LightmapDecoder downscales to this value.
        LightmapDecoder.HdrMaxTextureSize = AtlasCap;

        string measured = "no previous bake (cold start)";
        if (actuals.Count > 0)
        {
            actuals.Sort();
            int under = 0; foreach (var a in actuals) if (a < TargetPropTexels * 0.5f) under++;
            measured = $"previous bake widths min={actuals[0]:0} median={actuals[actuals.Count / 2]:0} max={actuals[actuals.Count - 1]:0}, {under} under half-target";
        }
        log($"density policy ({texelsPerUnit} texels/unit -> props>={TargetPropTexels:0}, arch<={TargetArchTexels:0}, atlas<={AtlasCap}): " +
            $"{result.Count} renderers, {changed} adjusted, {converged} already on target. {measured}.");

        result.Sort((a, b) => a.ActualWidth.CompareTo(b.ActualWidth));
        int shown = 0;
        foreach (var e in result)
        {
            if (shown++ >= 5) break;
            log($"  {e.Name}/{e.MeshName} [{(e.Architecture ? "arch" : "prop")}]: had {e.ActualWidth:0} texels, want {e.TargetWidth:0}; " +
                $"scaleInLightmap {e.ScaleBefore:0.##} -> {e.ScaleAfter:0.##}");
        }
        return result;
    }

    /// <summary>Gutter (bake texels) LightmapPaddingPolicy should use for this mesh.</summary>
    public static int GutterFor(Mesh mesh, float twidth)
    {
        if (mesh == null) return PropGutterTexels;
        return Measure(mesh).Islands <= ArchitectureIslandLimit && twidth >= 128f
            ? ArchitectureGutterTexels : PropGutterTexels;
    }

    static MeshRenderer FindRenderer(string goName, string meshName)
    {
        foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
        {
            if (r.gameObject.name != goName) continue;
            var f = r.GetComponent<MeshFilter>();
            if (f != null && f.sharedMesh != null && f.sharedMesh.name == meshName) return r;
        }
        return null;
    }

    static MeshInfo Measure(Mesh mesh)
    {
        if (_meshCache.TryGetValue(mesh, out var cached))
            return cached;

        var info = new MeshInfo { Islands = 1 };
        try
        {
            var tris = mesh.triangles;
            int nv = mesh.vertexCount;
            var parent = new int[nv];
            for (int i = 0; i < nv; i++) parent[i] = i;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                Union(parent, tris[i], tris[i + 1]);
                Union(parent, tris[i + 1], tris[i + 2]);
            }
            var roots = new HashSet<int>();
            for (int i = 0; i < nv; i++) roots.Add(Find(parent, i));
            info.Islands = roots.Count;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[LightmapDensityPolicy] could not measure mesh '{mesh.name}': {ex.Message}");
        }

        _meshCache[mesh] = info;
        return info;
    }

    static int Find(int[] parent, int x)
    {
        while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
        return x;
    }

    static void Union(int[] parent, int a, int b)
    {
        int ra = Find(parent, a), rb = Find(parent, b);
        if (ra != rb) parent[ra] = rb;
    }

    /// <summary>Bakery's area measure: sum of |cross| over world-space triangles (twice the true area).</summary>
    static float BakeryArea(Mesh mesh, Transform transform)
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
