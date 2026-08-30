using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

// LightmapBrightnessProbe.cs — bake-independent brightness statistics (2026-08-31).
//
// Answers "is this bake brighter than that one, and by how much?" with numbers that do not
// depend on atlas layout: for every lightmapped MeshRenderer it takes the UV2 islands
// (bounding boxes in atlas texels), shrinks each box by a few texels so gutters and seams are
// excluded, and averages the linear luminance of the texels inside. Per-renderer means are
// then combined weighted by island area. Run it after a Bakery bake and after a Unity
// Progressive bake of the same scene and the ratio of the two overall means is the gain that
// makes them match.
//
// Usage (execute_code / C# console):  return LightmapBrightnessProbe.Report(12);
public static class LightmapBrightnessProbe
{
    public struct RendererStat
    {
        public string Name;
        public int Lightmap;
        public long Texels;
        public double MeanLuma;
        public double MeanR, MeanG, MeanB;
    }

    /// <summary>Texels trimmed off every island box edge before sampling.</summary>
    public static int Shrink = 4;

    public static string Report(int topN = 12)
    {
        var stats = Measure(out double overall, out Color overallRgb, out long total);
        var sb = new StringBuilder();
        sb.Append($"brightness probe: {stats.Count} renderer(s), {total} texel(s); overall mean luma={overall:0.0000} rgb=({overallRgb.r:0.0000},{overallRgb.g:0.0000},{overallRgb.b:0.0000})\n");
        foreach (var s in stats.OrderByDescending(s => s.Texels).Take(topN))
            sb.Append($"  {s.Name} (lm{s.Lightmap}) texels={s.Texels} luma={s.MeanLuma:0.0000} rgb=({s.MeanR:0.000},{s.MeanG:0.000},{s.MeanB:0.000})\n");
        return sb.ToString();
    }

    public static List<RendererStat> Measure(out double overallLuma, out Color overallRgb, out long totalTexels)
    {
        var result = new List<RendererStat>();
        overallLuma = 0; overallRgb = Color.black; totalTexels = 0;
        var lightmaps = LightmapSettings.lightmaps;
        if (lightmaps == null || lightmaps.Length == 0) return result;

        var pixels = new Dictionary<int, Color[]>();
        var sizes = new Dictionary<int, Vector2Int>();
        for (int i = 0; i < lightmaps.Length; i++)
        {
            var tex = lightmaps[i].lightmapColor;
            if (tex == null) continue;
            pixels[i] = ReadLinear(tex);
            sizes[i] = new Vector2Int(tex.width, tex.height);
        }

        double sumL = 0, sumR = 0, sumG = 0, sumB = 0;
        foreach (var r in UnityEngine.Object.FindObjectsOfType<MeshRenderer>())
        {
            if (r == null || r.lightmapIndex < 0 || !pixels.ContainsKey(r.lightmapIndex)) continue;
            var mf = r.GetComponent<MeshFilter>();
            var mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null) continue;
            var uv2 = mesh.uv2;
            if (uv2 == null || uv2.Length != mesh.vertexCount) continue;

            var size = sizes[r.lightmapIndex];
            var px = pixels[r.lightmapIndex];
            long texels = 0; double l = 0, cr = 0, cg = 0, cb = 0;
            foreach (var box in IslandBoxes(mesh, uv2, r.lightmapScaleOffset, size))
            {
                int x0 = box.xMin + Shrink, x1 = box.xMax - Shrink, y0 = box.yMin + Shrink, y1 = box.yMax - Shrink;
                if (x1 < x0 || y1 < y0)
                {
                    // Tiny island: fall back to its centre texel so small props still count.
                    x0 = x1 = (box.xMin + box.xMax) / 2; y0 = y1 = (box.yMin + box.yMax) / 2;
                }
                for (int y = Math.Max(0, y0); y <= Math.Min(size.y - 1, y1); y++)
                    for (int x = Math.Max(0, x0); x <= Math.Min(size.x - 1, x1); x++)
                    {
                        var c = px[y * size.x + x];
                        l += 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b;
                        cr += c.r; cg += c.g; cb += c.b; texels++;
                    }
            }
            if (texels == 0) continue;
            result.Add(new RendererStat { Name = r.name, Lightmap = r.lightmapIndex, Texels = texels, MeanLuma = l / texels, MeanR = cr / texels, MeanG = cg / texels, MeanB = cb / texels });
            sumL += l; sumR += cr; sumG += cg; sumB += cb; totalTexels += texels;
        }
        if (totalTexels > 0)
        {
            overallLuma = sumL / totalTexels;
            overallRgb = new Color((float)(sumR / totalTexels), (float)(sumG / totalTexels), (float)(sumB / totalTexels), 1f);
        }
        return result;
    }

    static IEnumerable<RectInt> IslandBoxes(Mesh mesh, Vector2[] uv2, Vector4 so, Vector2Int size)
    {
        int n = uv2.Length;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        var byPos = new Dictionary<long, int>();
        for (int i = 0; i < n; i++)
        {
            long key = ((long)Mathf.RoundToInt(uv2[i].x * 65536f) << 32) ^ (uint)Mathf.RoundToInt(uv2[i].y * 65536f);
            if (byPos.TryGetValue(key, out int first)) Union(parent, first, i); else byPos[key] = i;
        }
        var used = new HashSet<int>();
        for (int s = 0; s < mesh.subMeshCount; s++)
        {
            var tris = mesh.GetTriangles(s);
            for (int i = 0; i + 2 < tris.Length; i += 3) { Union(parent, tris[i], tris[i + 1]); Union(parent, tris[i], tris[i + 2]); used.Add(tris[i]); used.Add(tris[i + 1]); used.Add(tris[i + 2]); }
        }
        var boxes = new Dictionary<int, RectInt>();
        for (int i = 0; i < n; i++)
        {
            if (!used.Contains(i)) continue;
            int root = Find(parent, i);
            int x = Mathf.RoundToInt((uv2[i].x * so.x + so.z) * size.x);
            int y = Mathf.RoundToInt((uv2[i].y * so.y + so.w) * size.y);
            if (boxes.TryGetValue(root, out var b))
                boxes[root] = new RectInt(Math.Min(b.xMin, x), Math.Min(b.yMin, y), Math.Max(b.xMax, x) - Math.Min(b.xMin, x), Math.Max(b.yMax, y) - Math.Min(b.yMin, y));
            else boxes[root] = new RectInt(x, y, 0, 0);
        }
        return boxes.Values;
    }

    static int Find(int[] p, int i) { while (p[i] != i) { p[i] = p[p[i]]; i = p[i]; } return i; }
    static void Union(int[] p, int a, int b) { a = Find(p, a); b = Find(p, b); if (a != b) p[b] = a; }

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
