using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

// 2026-08-30 (per Tanossy: "we need a memory-clearing step before starting work"): explicit
// memory hygiene at the start of every scene send, called from one line at the top of
// SceneConverter.ConvertScene(pass).
//
// Why: Editor.log for the 2026-08-30 session shows every full send growing Unity's native
// "used heap" from ~2.3 GB to ~15.8 GB ("Request Asset Garbage Collect because used heap size
// increased from 2.28 GB to 15.80 GB") and the automatic asset GC that follows freeing NOTHING
// ("Memory consumption went from 15.80 GB to 15.80 GB"). The growth is per send and cumulative
// within one Editor session: four failed sends plus a 4096^2 Bakery bake (27.6 GB) later, the
// fifth send pushed ALLOC_DEFAULT to 112 GB on a 64 GB machine and the Editor died with
// "Could not allocate memory: System out of memory!" (same failure the 2026-07-27 notes record
// at 2.9 -> 15.5 GB). The exact holder of that memory is not yet identified, so this does two
// things that are correct regardless of the culprit:
//
//  1. Forces a managed GC + finalizers BEFORE Unity's asset GC. Unity's automatic
//     "Request Asset Garbage Collect" only frees native objects whose managed wrappers are
//     already dead; a Texture2D created with `new` and simply dropped (see the readable-copy
//     path in Texture2DConverter.ConvertTexture2D) keeps its native memory until Mono has
//     collected the wrapper, which the automatic pass does not guarantee. Running the managed
//     GC first, then EditorUtility.UnloadUnusedAssetsImmediate, gives the asset GC a chance to
//     actually reclaim them.
//  2. Logs the total allocated memory before and after, so the next Editor.log says in plain
//     numbers whether this step freed anything - if it didn't, the memory is referenced by
//     something live (converter fields, cached messages, ...) and that is where to look next.
//
// Deliberately NOT called between individual asset uploads (UnloadUnusedAssetsImmediate is
// slow and would stall the WebSocket send loop); once per send is the right cadence.
public static class ConversionMemoryHygiene
{
    /// <summary>Set false to skip the hygiene pass (e.g. to A/B its effect against Editor.log).</summary>
    public static bool Enabled = true;

    public static void BeforeSend(string reason)
    {
        if (!Enabled)
            return;

        long before = Profiler.GetTotalAllocatedMemoryLong();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            System.GC.Collect();

            // includeMonoReferencesAsRoots: true - only unload what nothing managed points at.
            EditorUtility.UnloadUnusedAssetsImmediate(true);
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[ResoniteSDK] Memory hygiene ({reason}) failed: {ex.Message}");
            return;
        }

        long after = Profiler.GetTotalAllocatedMemoryLong();
        sw.Stop();

        Debug.Log($"[ResoniteSDK] Memory hygiene ({reason}): {before / 1073741824.0:0.00} GB -> {after / 1073741824.0:0.00} GB " +
            $"(freed {(before - after) / 1073741824.0:0.00} GB in {sw.ElapsedMilliseconds} ms).");
    }
}
