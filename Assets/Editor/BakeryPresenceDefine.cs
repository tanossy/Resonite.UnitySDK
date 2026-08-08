// BakeryPresenceDefine.cs
//
// ダイダロス作 — Bakery導入有無を自動検出し、`BAKERY_INCLUDED` スクリプティング定義
// シンボルを管理するブートストラップ。LightmapTestHarness.cs / BakeryTempObjectSuppression.cs
// の `#if BAKERY_INCLUDED` ガードはこのシンボルを前提にしている。
//
// ⚠ 本ファイル自体は Bakery型を一切直接参照しない（#if の外側に常に置かれ、Bakery導入有無に
// 関わらず毎回コンパイルされる必要があるため）。検出は AppDomain.CurrentDomain.GetAssemblies()
// を走査し、型 "ftRenderLightmap"（Bakery導入時の実在確認済み型: 名前空間なし。
// Assets/Editor/x64/Bakery/scripts/ftRenderLightmap.cs — LightmapTestHarness.cs の header
// comment 参照）を `asm.GetType("ftRenderLightmap")` の文字列名探索でのみ検出する。
// `typeof(ftRenderLightmap)` のような直接参照は絶対に使わない。
//
// --- スクリプティング定義シンボルAPI（実物確認済み）---
// Unity 2022.3.22f1 の UnityEditor.CoreModule.dll を ikdasm
// (Editor/Data/MonoBleedingEdge/lib/mono/4.5/ikdasm.exe) で逆アセンブルし、以下を確認した:
//   EditorUserBuildSettings.selectedBuildTargetGroup
//     .property public static, get_selectedBuildTargetGroup() は "public hidebysig
//     specialname static" — 外部アセンブリから直接呼べる。
//     （対照的に activeBuildTargetGroup の getter は "assembly"(=internal) 可視性のため
//     不可 — 実際に確認して除外した。）
//   NamedBuildTarget.FromBuildTargetGroup(BuildTargetGroup)
//     "public hidebysig static" — 外部アセンブリから直接呼べる。
//   PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget, out string[] defines)
//     "public hidebysig static void" — 外部アセンブリから直接呼べる。
//   PlayerSettings.SetScriptingDefineSymbols(NamedBuildTarget, string[] defines)
//     "public hidebysig static void" — 外部アセンブリから直接呼べる。
// 旧 API (...ForGroup(BuildTargetGroup) 系) も存在し使用可能だが、2022.3での正規の
// (非-ForGroup) API がそのまま使えることを確認済みのためこちらを採用した。
//
// --- 差分がある時だけSetする（無限リコンパイル防止）---
// 現在のシンボル配列に "BAKERY_INCLUDED" が含まれているか否かと検出結果を比較し、
// 一致していれば PlayerSettings.SetScriptingDefineSymbols を一切呼ばない。
// SetScriptingDefineSymbols は実際に値が変わった時のみスクリプト再コンパイルを
// トリガーするため、これにより「毎エディタ起動/毎ドメインリロードで無限に
// リコンパイルが走る」事態を防ぐ。
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

[InitializeOnLoad]
static class BakeryPresenceDefine
{
    const string Symbol = "BAKERY_INCLUDED";

    // Bakeryの型名。文字列のみで保持し、コード中のどこにも typeof(ftRenderLightmap) の
    // ような直接参照を書かない（このファイル自体がBakery非導入環境でも常にコンパイル
    // される前提のため）。
    const string BakeryProbeTypeName = "ftRenderLightmap";

    static BakeryPresenceDefine()
    {
        Apply();
    }

    static void Apply()
    {
        bool bakeryPresent = DetectBakeryPresent();

        var namedTarget = NamedBuildTarget.FromBuildTargetGroup(EditorUserBuildSettings.selectedBuildTargetGroup);
        PlayerSettings.GetScriptingDefineSymbols(namedTarget, out var defines);

        bool hasSymbol = Array.IndexOf(defines, Symbol) >= 0;
        if (hasSymbol == bakeryPresent)
            return; // Already in the correct state — do nothing (avoids an unnecessary recompile).

        string[] newDefines = bakeryPresent
            ? defines.Append(Symbol).ToArray()
            : defines.Where(d => d != Symbol).ToArray();

        PlayerSettings.SetScriptingDefineSymbols(namedTarget, newDefines);

        Debug.Log($"[BakeryPresenceDefine] Bakery {(bakeryPresent ? "detected" : "not detected")} " +
            $"-> {Symbol} {(bakeryPresent ? "added to" : "removed from")} scripting define symbols " +
            $"for '{namedTarget.TargetName}'. Recompiling...");
    }

    // Purely a string-name type lookup across already-loaded assemblies — never
    // `typeof(ftRenderLightmap)`. This is the one method in the whole Bakery-optional
    // codebase that must compile identically whether or not Bakery is installed, by
    // construction (it IS the detector), so it cannot reference the Bakery type directly.
    static bool DetectBakeryPresent()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type found;
            try
            {
                found = asm.GetType(BakeryProbeTypeName);
            }
            catch
            {
                // A small number of assemblies (dynamic/reflection-emit, some dependency
                // shims) can throw on GetType lookups rather than returning null — skip
                // those rather than letting a single bad assembly abort detection for the
                // whole AppDomain.
                continue;
            }

            if (found != null)
                return true;
        }

        return false;
    }
}
