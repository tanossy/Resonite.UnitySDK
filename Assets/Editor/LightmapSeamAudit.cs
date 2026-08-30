using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

// LightmapSeamAudit.cs — post-bake lightmap seam audit (2026-08-30).
//
// Mechanises the measurement that found the wall-corner bright line (see
// LightmapPaddingPolicy.cs's header for the story): for every lightmapped MeshRenderer it
//   1. splits the mesh's UV2 into islands (triangles connected through shared UV2 vertices),
//   2. maps each island's bounding box into atlas texels (uv2 * lightmapScaleOffset * size),
//   3. measures the gutter to the nearest other island of the same mesh AND to the nearest
//      other renderer's lightmap rect, and
//   4. reads the baked atlas itself to compare the luminance just INSIDE each island edge with
//      the luminance just OUTSIDE it (2 texels out) — a bright neighbour bleeding across a
//      too-small gutter shows up as a high outside/inside ratio.
// Findings above the thresholds are written to the harness result log so the next bake's
// log says in numbers whether the padding policy worked, instead of someone spotting a
// line in VR.
public static class LightmapSeamAudit
{
    /// <summary>Gutter below this many atlas texels is reported.</summary>
    public static int MinGutterTexels = 8;

    /// <summary>Outside/inside luminance ratio above this is reported (only when combined with a small gutter).</summary>
    public static float ContrastRatio = 6f;

    /// <summary>Cap on reported findings per audit, so a bad bake doesn't flood the log.</summary>
    public static int MaxFindings = 40;

    struct Island
    {
        public int MinX, MinY, MaxX, MaxY; // atlas texels, inclusive
        public int TriangleCount;
    }

    public static void RunAndLog(Action<string> log)
    {
        log = log ?? (s => Debug.Log("[LightmapSeamAudit] " + s));
        try
        {
            log(Run());
        }
        catch (Exception ex)
        {
            log("seam audit failed: " + ex);
        }
    }

    public static string Run()
    {
        var lightmaps = LightmapSettings.lightmaps;
        if (lightmaps == null || lightmaps.Length == 0)
            return "seam audit: no lightmaps in LightmapSettings.";

        var sb = new StringBuilder();
        sb.Append($"seam audit: {lightmaps.Length} lightmap(s), thresholds gutter<{MinGutterTexels}px & contrast>{ContrastRatio:0}x\n");

        // Decode each atlas once (BC6H etc. are not CPU-readable; a Blit round trip is).
        var decoded = new Dictionary<int, Color[]>();
        var sizes = new Dictionary<int, Vector2Int>();
        for (int i = 0; i < lightmaps.Length; i++)
        {
            var tex = lightmaps[i].lightmapColor;
            if (tex == null) continue;
            decoded[i] = ReadLinear(tex);
            sizes[i] = new Vector2Int(tex.width, tex.height);
        }

        // Per-renderer island lists (for cross-renderer gutter checks we use the whole rect).
        var renderers = UnityEngine.Object.FindObjectsOfType<MeshRenderer>();
        var rects = new List<(MeshRenderer r, int lm, RectInt rect)>();
        var islandsByRenderer = new Dictionary<MeshRenderer, List<Island>>();

        foreach (var r in renderers)
        {
            if (r == null || r.lightmapIndex < 0 || r.lightmapIndex >= lightmaps.Length) continue;
            if (!sizes.TryGetValue(r.lightmapIndex, out var size)) continue;
            var mf = r.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null) continue;
            var uv2 = mesh.uv2;
            if (uv2 == null || uv2.Length != mesh.vertexCount) continue;

            var islands = BuildIslands(mesh, uv2, r.lightmapScaleOffset, size);
            islandsByRenderer[r] = islands;

            var so = r.lightmapScaleOffset;
            var rect = new RectInt(
                Mathf.FloorToInt(so.z * size.x), Mathf.FloorToInt(so.w * size.y),
                Mathf.CeilToInt(so.x * size.x), Mathf.CeilToInt(so.y * size.y));
            rects.Add((r, r.lightmapIndex, rect));
        }

        int findings = 0, checkedIslands = 0;

        foreach (var pair in islandsByRenderer)
        {
            var r = pair.Key;
            var islands = pair.Value;
            int lm = r.lightmapIndex;
            var size = sizes[lm];
            var pixels = decoded.TryGetValue(lm, out var px) ? px : null;

            for (int i = 0; i < islands.Count; i++)
            {
                var a = islands[i];
                checkedIslands++;

                // Nearest other island of the same mesh.
                int gutter = int.MaxValue;
                for (int j = 0; j < islands.Count; j++)
                {
                    if (i == j) continue;
                    gutter = Math.Min(gutter, RectGap(a, islands[j]));
                }

                // Nearest other renderer's rect on the same atlas.
                string neighbour = null;
                foreach (var other in rects)
                {
                    if (other.r == r || other.lm != lm) continue;
                    var b = new Island { MinX = other.rect.xMin, MinY = other.rect.yMin, MaxX = other.rect.xMax - 1, MaxY = other.rect.yMax - 1 };
                    int g = RectGap(a, b);
                    if (g < gutter) { gutter = g; neighbour = other.r.name; }
                }

                if (gutter >= MinGutterTexels)
                    continue;

                // Contrast: for each edge, mean luminance 1 texel inside vs 2 texels outside.
                float worst = 0f; string worstEdge = "";
                if (pixels != null)
                {
                    foreach (var edge in new[] { "left", "right", "bottom", "top" })
                    {
                        float inside = EdgeMean(pixels, size, a, edge, -1);
                        float outside = EdgeMean(pixels, size, a, edge, +2);
                        float ratio = outside / Mathf.Max(inside, 1e-4f);
                        if (ratio > worst) { worst = ratio; worstEdge = edge; }
                    }
                }

                if (worst < ContrastRatio)
                    continue;

                findings++;
                if (findings <= MaxFindings)
                    sb.Append($"  SEAM RISK {r.name} (lm{lm}) island#{i} [{a.MinX}-{a.MaxX}]x[{a.MinY}-{a.MaxY}] tris={a.TriangleCount}: " +
                        $"gutter={gutter}px{(neighbour != null ? " (next to " + neighbour + ")" : " (same mesh)")}, " +
                        $"contrast {worst:0.0}x at {worstEdge} edge\n");
            }
        }

        sb.Append($"seam audit: {checkedIslands} island(s) checked, {findings} finding(s){(findings > MaxFindings ? $" ({MaxFindings} shown)" : "")}.");
        return sb.ToString();
    }

    // ---- islands --------------------------------------------------------------------------

    static List<Island> BuildIslands(Mesh mesh, Vector2[] uv2, Vector4 so, Vector2Int size)
    {
        int n = uv2.Length;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;

        // Vertices that share a UV2 position (quantised) belong to the same island even if
        // the mesh split them (hard edges / different normals).
        var byPos = new Dictionary<long, int>();
        for (int i = 0; i < n; i++)
        {
            long key = ((long)Mathf.RoundToInt(uv2[i].x * 65536f) << 32) ^ (uint)Mathf.RoundToInt(uv2[i].y * 65536f);
            if (byPos.TryGetValue(key, out int first)) Union(parent, first, i);
            else byPos[key] = i;
        }

        var triCount = new Dictionary<int, int>();
        for (int s = 0; s < mesh.subMeshCount; s++)
        {
            var tris = mesh.GetTriangles(s);
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                Union(parent, tris[i], tris[i + 1]);
                Union(parent, tris[i], tris[i + 2]);
            }
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                int root = Find(parent, tris[i]);
                triCount[root] = triCount.TryGetValue(root, out int c) ? c + 1 : 1;
            }
        }

        var boxes = new Dictionary<int, Island>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(parent, i);
            if (!triCount.ContainsKey(root)) continue; // isolated vertex
            int x = Mathf.RoundToInt((uv2[i].x * so.x + so.z) * size.x);
            int y = Mathf.RoundToInt((uv2[i].y * so.y + so.w) * size.y);
            if (boxes.TryGetValue(root, out var b))
            {
                b.MinX = Math.Min(b.MinX, x); b.MaxX = Math.Max(b.MaxX, x);
                b.MinY = Math.Min(b.MinY, y); b.MaxY = Math.Max(b.MaxY, y);
                boxes[root] = b;
            }
            else
                boxes[root] = new Island { MinX = x, MaxX = x, MinY = y, MaxY = y, TriangleCount = triCount[root] };
        }

        return new List<Island>(boxes.Values);
    }

    static int Find(int[] p, int i) { while (p[i] != i) { p[i] = p[p[i]]; i = p[i]; } return i; }
    static void Union(int[] p, int a, int b) { a = Find(p, a); b = Find(p, b); if (a != b) p[b] = a; }

    /// <summary>Axis-aligned gap between two texel boxes (0 = touching/overlapping).</summary>
    static int RectGap(Island a, Island b)
    {
        int dx = Math.Max(0, Math.Max(b.MinX - a.MaxX - 1, a.MinX - b.MaxX - 1));
        int dy = Math.Max(0, Math.Max(b.MinY - a.MaxY - 1, a.MinY - b.MaxY - 1));
        return Math.Max(dx, dy);
    }

    // ---- atlas sampling ---------------------------------------------------------------------

    static float EdgeMean(Color[] px, Vector2Int size, Island a, string edge, int offset)
    {
        // offset < 0: that many texels INSIDE the box (left/bottom edges: MinX - offset = MinX + 1;
        // right/top edges: MaxX + offset = MaxX - 1); offset > 0: that many texels OUTSIDE.
        float sum = 0f; int count = 0;
        void Add(int x, int y)
        {
            if (x < 0 || y < 0 || x >= size.x || y >= size.y) return;
            var c = px[y * size.x + x];
            sum += 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b; count++;
        }
        switch (edge)
        {
            case "left":   for (int y = a.MinY; y <= a.MaxY; y++) Add(a.MinX - offset, y); break;
            case "right":  for (int y = a.MinY; y <= a.MaxY; y++) Add(a.MaxX + offset, y); break;
            case "bottom": for (int x = a.MinX; x <= a.MaxX; x++) Add(x, a.MinY - offset); break;
            case "top":    for (int x = a.MinX; x <= a.MaxX; x++) Add(x, a.MaxY + offset); break;
        }
        return count > 0 ? sum / count : 0f;
    }

    static Color[] ReadLinear(Texture2D source)
    {
        var rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        var prev = RenderTexture.active;
        Texture2D read = null;
        try
        {
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            read = new Texture2D(source.width, source.height, TextureFormat.RGBAFloat, false, true);
            read.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            read.Apply();
            return read.GetPixels();
        }
        finally
        {
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            if (read != null) UnityEngine.Object.DestroyImmediate(read);
        }
    }
}
