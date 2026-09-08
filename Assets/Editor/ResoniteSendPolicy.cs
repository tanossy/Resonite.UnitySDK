using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

// ResoniteSendPolicy.cs — one send button that picks the conversion pass itself (2026-09-08).
//
// THE PROBLEM:
//   The Lightmap Baker panel offered "Send Meshes Only", "Send Materials Only" and "Send
//   Lightmaps Only" and no Full send at all, so the obvious thing to click was always a
//   partial pass. Partial passes are DELTA updates: they only work against a tree that a Full
//   send already built in the SAME ResoniteLink session, with the converter not corrupted.
//   Neither precondition is visible in the UI. On 2026-09-08 every partial send attempted -
//   two Meshes Only, three Lightmaps Only - failed, and each failure corrupted the conversion
//   state so the NEXT Full send had to rebuild the whole tree from scratch.
//
//   They also save the wrong thing. A Full send takes minutes, almost all of it Unity-side
//   lightmap variant material generation, which the partial passes do not skip. The network
//   part measured 279 MB in 34 s; a partial pass shaves maybe 20 s off that.
//
// WHAT THIS DOES:
//   SendToResonite() looks at the live session and picks the pass. It sends Lightmaps Only
//   only when it can prove that is safe - same session, healthy converter, and a scene
//   structure signature identical to the last send that succeeded - and Full otherwise.
//   SendLightmapsOnly() is the explicit button; it applies the same proof and falls back to a
//   Full send with a stated reason instead of failing.
//
//   The signature deliberately EXCLUDES lightmapIndex and lightmapScaleOffset, because those
//   are exactly what a lightmap pass carries. It covers what a lightmap pass does not: which
//   renderers exist, where they are, their meshes and materials, and the lights.
public static class ResoniteSendPolicy
{
    const string KeySession = "ResoniteSendPolicy.LastGoodSession";
    const string KeySignature = "ResoniteSendPolicy.LastGoodSignature";

    public enum Pass { Full, LightmapsOnly }

    public struct Decision
    {
        public bool CanSend;
        public Pass Chosen;
        public string Reason;
    }

    /// <summary>The panel's main button: send, letting the policy choose the pass.</summary>
    public static void SendToResonite(Action<string> log)
    {
        log = log ?? (s => Debug.Log("[ResoniteSendPolicy] " + s));
        var decision = Decide();
        if (!decision.CanSend)
        {
            log("send: " + decision.Reason);
            return;
        }
        Run(decision.Chosen, decision.Reason, log);
    }

    /// <summary>
    /// The explicit "Send Lightmaps Only" button. Honours the request when it is provably
    /// safe; otherwise says why and sends Full rather than failing halfway.
    /// </summary>
    public static void SendLightmapsOnly(Action<string> log)
    {
        log = log ?? (s => Debug.Log("[ResoniteSendPolicy] " + s));
        var decision = Decide();
        if (!decision.CanSend)
        {
            log("lightmaps only: " + decision.Reason);
            return;
        }
        if (decision.Chosen == Pass.LightmapsOnly)
        {
            Run(Pass.LightmapsOnly, decision.Reason, log);
            return;
        }
        log("lightmaps only: not safe here - " + decision.Reason + " Sending the full scene instead.");
        Run(Pass.Full, "fallback from lightmaps only", log);
    }

    /// <summary>What would be sent, and why. Safe to call from OnGUI for a status line.</summary>
    public static Decision Decide()
    {
        var window = FindLinkWindow();
        if (window == null)
            return new Decision { CanSend = false, Reason = "the Resonite SDK Manager window is not open (Resonite SDK > Open Resonite SDK Manager)." };

        var stateProp = window.GetType().GetProperty("State");
        var state = stateProp != null ? stateProp.GetValue(window, null) : null;
        if (state == null || state.ToString() != "Connected")
            return new Decision { CanSend = false, Reason = $"ResoniteLink is not connected (state = {state}). Connect first." };

        string sessionId = window.GetType().GetProperty("UniqueSessionId")?.GetValue(window, null) as string;
        object converter = window.GetType()
            .GetField("_converter", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(window);

        bool healthy = converter != null;
        if (healthy)
        {
            var corrupted = converter.GetType().GetProperty("IsCorrupted")?.GetValue(converter, null);
            healthy = corrupted is bool && !(bool)corrupted;
        }

        string lastSession = SessionState.GetString(KeySession, "");
        string lastSignature = SessionState.GetString(KeySignature, "");
        string signature = SceneStructureSignature();

        if (!healthy)
            return new Decision { CanSend = true, Chosen = Pass.Full, Reason = converter == null
                ? "no conversion state yet in this session." : "the conversion state is marked corrupted by an earlier failure." };

        if (string.IsNullOrEmpty(lastSession) || lastSession != sessionId)
            return new Decision { CanSend = true, Chosen = Pass.Full, Reason = $"no successful send recorded for session {sessionId}." };

        if (lastSignature != signature)
            return new Decision { CanSend = true, Chosen = Pass.Full, Reason = "the scene structure changed since the last successful send (renderers, transforms, meshes, materials or lights)." };

        return new Decision { CanSend = true, Chosen = Pass.LightmapsOnly, Reason = $"session {sessionId} already holds a matching tree, so only the lightmaps need to go." };
    }

    static void Run(Pass pass, string reason, Action<string> log)
    {
        log($"send: {pass} - {reason}");

        PreSendHygiene(log);

        if (pass == Pass.Full)
            LightmapTestHarness.Convert();
        else
            LightmapTestHarness.ConvertLightmapsOnly();

        // Record only when the conversion state survived; a corrupted converter means the tree
        // in Resonite no longer matches this scene and the next send must be Full.
        var window = FindLinkWindow();
        object converter = window?.GetType()
            .GetField("_converter", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(window);
        bool ok = converter != null;
        if (ok)
        {
            var corrupted = converter.GetType().GetProperty("IsCorrupted")?.GetValue(converter, null);
            ok = corrupted is bool && !(bool)corrupted;
        }

        if (ok)
        {
            SessionState.SetString(KeySession, window.GetType().GetProperty("UniqueSessionId")?.GetValue(window, null) as string ?? "");
            SessionState.SetString(KeySignature, SceneStructureSignature());
            log("send: conversion state is healthy; recorded as the baseline for the next send.");
        }
        else
        {
            SessionState.SetString(KeySession, "");
            SessionState.SetString(KeySignature, "");
            log("send: the conversion state did not survive - the next send will be a full one.");
        }
    }


    /// <summary>
    /// Removes the two kinds of leftover that make a send fail with a dangling reference.
    ///
    /// 1. Orphan wrapper components. ParticleSystemConverter.Cleanup() only destroys its
    ///    renderer and emitter wrappers, so the ParticleSystem / ParticleStyle / module
    ///    wrappers survive every converter rebuild and pile up - 78 components on one particle
    ///    GameObject were measured on 2026-09-08. An orphan ParticleStyle still points at a
    ///    destroyed renderer, and that reference goes out in the batch.
    /// 2. Asset converter GameObjects that ended up outside __UnityAssets. CleanupAssetConversionRoots()
    ///    only walks the two known roots, so a stray "Material - ..." at the scene root keeps
    ///    referencing textures that no longer exist.
    ///
    /// Resonite answers a batch holding an unresolvable reference with a success batchResponse
    /// AND a second failure response carrying the same message id; ResoniteLink.dll treats that
    /// unknown id as fatal and closes its own socket, so the next asset upload dies on a
    /// disposed WebSocket and every remaining provider URL stays empty. Removing the source of
    /// the dangling reference is what keeps the send alive. See PARTHENON-318.
    /// </summary>
    static void PreSendHygiene(Action<string> log)
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (!scene.IsValid()) return;

        Type componentBase = FindType("ResoniteComponent");
        Type converterBase = FindType("ResoniteComponentConverter");
        Type assetConverter = FindType("AssetConverter");
        if (componentBase == null || converterBase == null) return;

        int orphans = 0;
        foreach (var go in UnityEngine.Object.FindObjectsOfType<GameObject>())
        {
            if (go == null || go.scene != scene) continue;

            var components = go.GetComponents<Component>();
            bool hasConverter = false;
            foreach (var c in components)
                if (c != null && converterBase.IsAssignableFrom(c.GetType())) { hasConverter = true; break; }
            if (!hasConverter) continue;

            // Everything the live converters on this GameObject still point at.
            var live = new System.Collections.Generic.HashSet<Component>();
            foreach (var c in components)
            {
                if (c == null || !converterBase.IsAssignableFrom(c.GetType())) continue;
                foreach (var f in c.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    var v = f.GetValue(c) as Component;
                    if (v != null) live.Add(v);
                }
            }

            foreach (var c in components)
            {
                if (c == null || !componentBase.IsAssignableFrom(c.GetType())) continue;
                if (live.Contains(c)) continue;
                UnityEngine.Object.DestroyImmediate(c);
                orphans++;
            }
        }

        int strays = 0;
        foreach (var root in scene.GetRootGameObjects())
        {
            if (root == null) continue;
            if (root.name == "__UnityAssets" || root.name == "__UnitySkybox") continue;

            bool stray = root.name.StartsWith("Material - ")
                || root.name.StartsWith("Texture2D - ")
                || root.name.StartsWith("Mesh - ")
                || root.name.StartsWith("Cubemap - ");
            if (!stray && assetConverter != null)
                foreach (var c in root.GetComponents<Component>())
                    if (c != null && assetConverter.IsAssignableFrom(c.GetType())) { stray = true; break; }

            if (!stray) continue;
            UnityEngine.Object.DestroyImmediate(root);
            strays++;
        }

        if (orphans > 0 || strays > 0)
            log($"send: cleaned {orphans} orphan wrapper component(s) and {strays} stray asset-converter root(s) before sending.");
    }

    static Type FindType(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type t = null;
            try { t = asm.GetType(name); } catch { }
            if (t != null) return t;
        }
        return null;
    }

    /// <summary>
    /// Hash of everything a lightmap pass does NOT carry. lightmapIndex and lightmapScaleOffset
    /// are excluded on purpose: they change on every re-bake and are precisely what the
    /// lightmap pass updates.
    /// </summary>
    public static string SceneStructureSignature()
    {
        var sb = new StringBuilder(1 << 16);
        var renderers = UnityEngine.Object.FindObjectsOfType<MeshRenderer>();
        Array.Sort(renderers, (a, b) => string.CompareOrdinal(PathOf(a.transform), PathOf(b.transform)));
        foreach (var r in renderers)
        {
            if (r == null || !r.gameObject.activeInHierarchy) continue;
            sb.Append(PathOf(r.transform)).Append('|');
            var t = r.transform;
            sb.Append(t.position.ToString("F3")).Append(t.rotation.ToString("F3")).Append(t.lossyScale.ToString("F3")).Append('|');
            var mf = r.GetComponent<MeshFilter>();
            sb.Append(AssetKey(mf != null ? mf.sharedMesh : null)).Append('|');
            foreach (var m in r.sharedMaterials) sb.Append(AssetKey(m)).Append(',');
            sb.Append('\n');
        }

        var lights = UnityEngine.Object.FindObjectsOfType<Light>();
        Array.Sort(lights, (a, b) => string.CompareOrdinal(PathOf(a.transform), PathOf(b.transform)));
        foreach (var l in lights)
        {
            if (l == null || !l.gameObject.activeInHierarchy) continue;
            sb.Append(PathOf(l.transform)).Append('|').Append(l.type).Append('|')
              .Append(l.color.ToString("F3")).Append('|').Append(l.intensity.ToString("F3")).Append('|')
              .Append(l.range.ToString("F3")).Append('|').Append(l.shadows).Append('|')
              .Append(l.transform.rotation.ToString("F3")).Append('\n');
        }

        using (var md5 = MD5.Create())
        {
            var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            var hex = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) hex.Append(b.ToString("x2"));
            return hex.ToString();
        }
    }

    static string AssetKey(UnityEngine.Object o)
    {
        if (o == null) return "null";
        string guid;
        long localId;
        if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out guid, out localId))
            return guid + ":" + localId;
        return o.GetType().Name + ":" + o.name;
    }

    static string PathOf(Transform t)
    {
        var path = t.name;
        while (t.parent != null) { t = t.parent; path = t.name + "/" + path; }
        return path;
    }

    static EditorWindow FindLinkWindow()
    {
        Type type = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { type = asm.GetType("ResoniteLinkWindow"); }
            catch { type = null; }
            if (type != null) break;
        }
        if (type == null) return null;

        EditorWindow fallback = null;
        foreach (var o in Resources.FindObjectsOfTypeAll(type))
        {
            var w = o as EditorWindow;
            if (w == null) continue;
            var state = type.GetProperty("State")?.GetValue(w, null);
            if (state != null && state.ToString() == "Connected") return w;
            if (fallback == null) fallback = w;
        }
        return fallback;
    }
}
