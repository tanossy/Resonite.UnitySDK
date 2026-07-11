# Resonite Lightbake Importer

Bring **Unity-baked lighting — shadows, global illumination, and reflection probes — into Resonite**, using only Resonite's stock materials. No custom shaders required.

This is an addition to the [Resonite Unity SDK](https://github.com/Yellow-Dog-Man/Resonite.UnitySDK). Bake your scene in Unity, press **Send Current Scene** in the Resonite SDK Manager, and the baked look comes across.

---

## TL;DR

- Bake a scene in Unity (Progressive GPU lightmapper).
- Connect the Resonite SDK Manager and press **Send Current Scene**.
- Baked shadows / GI / reflections appear in Resonite on the stock `PBS_MultiUV_Metallic` material.
- An optional **Lightmap Pipeline** editor panel helps you dial in the bake settings that actually produce shadows.

---

## How it works (the key insight)

Resonite's renderer (Renderite, itself Unity-based) does **not** officially support custom shaders, so you cannot ship a Unity "lightmap shader" to Resonite. Many people conclude that baked lighting simply can't be moved over. It can — because of two facts most people miss:

### 1. Resonite has a "UV2 equivalent" — it is just named differently

A mesh can carry several UV channels. Unity and Resonite both do. The confusion is purely naming:

| | Main UV | Lightmap UV |
|---|---|---|
| UV channel index | 0 | **1** |
| Unity property name | `mesh.uv` | `mesh.uv2` — the name says "2" but it is channel **1** |
| Resonite name | UV0 | **UV1** |

What Unity calls `uv2` (the lightmap UV) is really the **second UV channel (index 1)**. Resonite's **UV1** is also the second UV channel (index 1). They are **the same channel**. Resonite never lacked a second UV set — Unity's historical `uv2` naming just makes it look that way.

### 2. Resonite's stock material has a "multiply" texture slot

The stock **`PBS_MultiUV_Metallic`** material has a **SecondaryAlbedoTexture** slot that is composited by **multiplication** (verified in the shader source: `c *= tex2D(_SecondaryAlbedo, ...)`), and it can be sampled with the **second UV set (SecondaryAlbedoUV = 1)**.

A Unity lightmap is exactly "a texture, indexed by the lightmap UV, holding baked light and shadow." So:

> **albedo (e.g. wood) × lightmap (baked light & shadow) = the fully lit appearance**

is reproduced with a stock Resonite material, no custom shader. Where a shadow was baked, the lightmap is dark, so the albedo is darkened there. Physically correct compositing.

### The pipeline (automatic, during Send Current Scene)

1. **Decode** the baked lightmap. Unity stores it as **BC6H** (HDR, GPU-compressed), which Resonite cannot ingest raw — it is GPU-blit-decoded to an **sRGB PNG** (also handles RGBAHalf / RGBM / DoubleLDR).
2. **Carry the atlas rectangle.** Each object uses only a sub-rect of the shared lightmap atlas (`lightmapScaleOffset`). That is copied into the material's **SecondaryAlbedoScale / Offset** so every object samples its own patch.
3. **Per-object material variants.** Because the scale/offset differs per object, each MeshRenderer gets its own generated `PBS_MultiUV_Metallic` variant.
4. **Transfer the second UV.** Unity's `uv2` is written to Resonite's **UV1**, so the SecondaryAlbedo samples the right place.

---

## What transfers, and what does not

| Element | Transfers? | Notes |
|---|---|---|
| Geometry + UVs | ✅ | |
| Baked shadows / GI (lightmaps) | ✅ | The core feature. |
| Baked **reflection probes** | ✅ | `ReflectionProbeConverter` carries the baked cubemap. Realtime probes re-render in Resonite; Custom mode is approximated. |
| Standard / albedo-based materials | ✅ | |
| **Light probes** (SH lighting for dynamic objects) | ❌ | No converter exists; the underlying `SphericalHarmonicsL2` binding is currently non-functional. |
| Post-processing (bloom, color grading, screen-space AO, …) | ❌ | Resonite handles post effects separately; a large part of a "look" that leans on post FX will change. |
| Poiyomi / custom / toon shaders | ⚠️ | Only the albedo maps across; rim, matcap, custom lighting effects do not. Expect a flatter result. |
| Realtime lights / realtime shadows | ❌ | Only what is baked into the lightmap comes over. |

### Known unknown

Verified with a **single lightmap atlas**. Large scenes (e.g. imported VRChat worlds) frequently use **several 2K/4K atlases** (`lightmapIndex > 0`). Multi-atlas behaviour is **not yet verified** — try it and check.

---

## Requirements & conditions

- **Unity 2022.3.22f1** (match the Resonite SDK / Renderite baseline).
- The scene must be baked with Unity's **Progressive** lightmapper (produces the lightmap atlas, `uv2`, and `lightmapScaleOffset`).
- Static/occluding objects must be **Contribute GI** (Static → Contribute Global Illumination) with valid lightmap UVs.
- Materials should be **albedo-based** (Standard-like) for the multiply compositing to read correctly.

---

## Already-baked scenes: bring them as-is

If a Unity scene is **already lightmapped and looks the way you want**, you do **not** need the panel, a test scene, or a re-bake. Just connect the SDK Manager and press **Send Current Scene** — the converter picks up the existing lightmap automatically. The panel is only for **creating or tuning** a bake.

---

## Installation

This feature lives in a fork of the official SDK:

- **Fork:** `https://github.com/tanossy/Resonite.UnitySDK` (branch `feature/baked-lightmap-import`)
- **Upstream:** `https://github.com/Yellow-Dog-Man/Resonite.UnitySDK`

1. Use the fork's Unity SDK as the base for your Resonite content project (same way you would use the official SDK), **or** copy the lightmap-import scripts under `Assets/ResoniteSDK/ComponentConverters/Unity Core/Rendering/` into an existing SDK install.
2. Open the project in Unity 2022.3.22f1.
3. The lightmap converter runs automatically during **Send Current Scene** — no extra setup.

> If/when this is merged upstream, no fork will be needed — the official SDK will carry it.

---

## Usage

### Normal path

1. Bake your scene in Unity (see recommended settings below).
2. Open **`Resonite SDK > Open Resonite SDK Manager`**, connect (AutoDiscovery or manual port).
3. Press **Send Current Scene**. Done.

### The Lightmap Pipeline panel (optional)

Menu: **`Resonite SDK > Lightmap Pipeline`**. It helps you get a bake that actually has shadows — the single hardest part.

- **Baker** — Unity Standard (Bakery is shown but greyed out unless Bakery is installed).
- **Quality Preset** — low / mid / high / custom (lightmap resolution & sample counts).
- **Lighting** (Unity Standard only) — five knobs: Ambient Brightness, Ambient Color, Shadow Strength, Sun Color, Sun Angle (elevation, azimuth). The Sun knobs target the scene's brightest enabled directional light; if there is none, they grey out.
- **Bake** — applies the Lighting knobs to the scene, then bakes.
- **Bake & Send** — bakes, then sends to Resonite automatically.

### Recommended bake settings (this is where people get stuck)

A scripted or freshly added light defaults to **Shadow Type = None**, so the lightmapper bakes illumination but **no shadows**. The settings that reliably produce clean, grounded shadows:

- Directional light: **Baked**, **Shadow Type = Soft**, Shadow Strength = 1.
- A **lower sun angle** (~35° elevation) throws longer, clearer shadows.
- **Ambient**: Flat, moderate (~0.12) — near-black ambient turns every surface the sun cannot reach pure black.
- Keep any **fill lights weak** — a strong fill floods the room and washes cast shadows out.
- **Ambient Occlusion ON** for contact shadows where objects meet the floor.
- Make sure objects **sit on** surfaces (not embedded), or their contact shadow hides inside the geometry.
- Objects that should cast/receive baked light must be **Contribute GI**.

---

## Bakery (optional)

An optional [Bakery](https://assetstore.unity.com/packages/tools/level-design/bakery-gpu-lightmapper-122218) path exists for users who own that asset (it re-atlases lightmap UVs and can give cleaner charts on some meshes). **Bakery is not required** — the Unity-standard path is fully self-contained. All Bakery code is compiled out when Bakery is absent, and the panel shows the Bakery option greyed out so you can see it becomes available if you install Bakery.

---

## Fork & upstream

This is a feature branch of the Yellow-Dog-Man SDK. The intent is to keep it in sync with upstream and, where appropriate, propose the lightmap-import converter for upstream inclusion so it becomes available to everyone without a fork. Contributions and issues welcome.

---

## License

The **baked-lightmap-import additions** in this repository (the lightmap converter, decoder, material cache, and the Lightmap Pipeline editor tooling) are released under the **MIT License**:

```
MIT License

Copyright (c) 2026 Tanossy

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

> **Note:** the underlying **Resonite Unity SDK** and **Bakery** are governed by their own respective licenses. This MIT grant covers only the lightmap-import additions, not the base SDK or any third-party asset. Bakery is a paid asset and is never redistributed here.

---

## Disclaimer

The Resonite Unity SDK is beta and does not convert every component. This tool covers baked lighting and reflection probes; results on complex production scenes will vary. Always keep a backup of your Unity project before large batch operations.

---
---

# Resonite Lightbake Importer（日本語）

**Unityで焼いたライティング（影・GI・リフレクションプローブ）を、Resonite標準マテリアルだけで持ち込む**ためのツールです。カスタムシェーダーは不要。

[Resonite Unity SDK](https://github.com/Yellow-Dog-Man/Resonite.UnitySDK) への追加機能です。Unityでシーンをベイクし、Resonite SDK Managerで **Send Current Scene** を押すだけで、焼いた見た目がResoniteに乗ります。

---

## 要点

- Unityでシーンをベイク（Progressive GPU）
- SDK Managerを接続して **Send Current Scene**
- 焼いた影・GI・反射が、標準マテリアル `PBS_MultiUV_Metallic` に乗って再現される
- 影の出るベイク設定を詰めるための **Lightmap Pipeline** パネル（任意）付き

---

## 仕組み（肝）

Resoniteのレンダラ（Renderite＝Unity製）は**公式にカスタムシェーダーを持ち込めない**ため、Unityの「ライトマップシェーダー」をそのまま送ることはできません。多くの人がここで「ベイクは移せない」と諦めます。でも移せます。見落とされがちな2つの事実があるからです。

### 1. ResoniteにもUV2相当はある — 呼び名が違うだけ

メッシュは複数のUVチャンネルを持てます。UnityもResoniteも同じ。混乱の原因は**名前**だけです。

| | メインUV | ライトマップUV |
|---|---|---|
| UVチャンネル番号 | 0 | **1** |
| Unityのプロパティ名 | `mesh.uv` | `mesh.uv2` ← 名前は"2"だが実体はチャンネル**1** |
| Resoniteの呼び名 | UV0 | **UV1** |

Unityが `uv2` と呼ぶライトマップUVは、実際には**2番目のチャンネル（index 1）**。Resoniteの **UV1** も2番目のチャンネル（index 1）。**同じ場所**です。Resoniteに2枚目UVが無かったわけではなく、Unityの歴史的な `uv2` 命名がそう錯覚させていただけ。

### 2. Resonite標準マテリアルに「乗算スロット」がある

標準マテリアル **`PBS_MultiUV_Metallic`** の **SecondaryAlbedoTexture** は**乗算**で合成され（シェーダー原文 `c *= tex2D(_SecondaryAlbedo, ...)` で確認）、**2枚目UV（SecondaryAlbedoUV = 1）**で貼れます。

Unityのライトマップとは「ライトマップUVで引く、焼いた光と影のテクスチャ」そのもの。だから——

> **アルベド（例：木目）× ライトマップ（焼いた光と影）＝ 陰影の付いた見た目**

を、カスタムシェーダー無し・標準マテリアルだけで再現できる。影の焼けた場所はライトマップが暗い→木目が暗くなる。物理的に正しい合成です。

### パイプライン（Send Current Scene 中に自動実行）

1. **デコード**：Unityは **BC6H**（HDR圧縮）で保存する。Resoniteはそのまま食えないので、GPU Blitで **sRGB PNG** に変換（RGBAHalf / RGBM / DoubleLDR も対応）。
2. **矩形の引き継ぎ**：各オブジェクトは共有atlasの一部矩形（`lightmapScaleOffset`）だけを使う。これを **SecondaryAlbedoScale / Offset** にコピーし、各自が自分の矩形を引くようにする。
3. **オブジェクトごとのマテリアル変種**：Scale/Offsetがオブジェクト別なので、各MeshRendererに専用の `PBS_MultiUV_Metallic` を生成。
4. **2枚目UVの転写**：Unityの `uv2` を Resoniteの **UV1** へ。

---

## 来るもの／来ないもの

| 要素 | 可否 | 補足 |
|---|---|---|
| ジオメトリ＋UV | ✅ | |
| 焼いた影・GI（ライトマップ） | ✅ | 本機能の核。 |
| 焼いた**リフレクションプローブ** | ✅ | `ReflectionProbeConverter` が焼いたキューブマップを転送。Realtimeは再レンダ、Customは近似。 |
| Standard系/アルベド主体マテリアル | ✅ | |
| **ライトプローブ**（動的物のSHライティング） | ❌ | 変換器が無く、`SphericalHarmonicsL2` バインディングが現状非機能。 |
| ポストプロセッシング（Bloom・色調補正・SSAO 等） | ❌ | Resonite側で別処理。ポスト依存の"雰囲気"は変わる。 |
| Poiyomi/カスタム/トゥーンシェーダー | ⚠️ | アルベドのみ移る。リム/マットキャップ/独自ライティングは来ず、平坦化する。 |
| リアルタイムライト・リアルタイム影 | ❌ | 焼き込まれた分だけ来る。 |

### 未検証の点

検証は**ライトマップ1枚**のシーン。大きいシーン（例：取り込んだVRChatワールド）は**2K/4Kを複数枚**使うことが多い（`lightmapIndex > 0`）。複数atlas時の挙動は**未検証**。実物で確認を。

---

## 前提条件

- **Unity 2022.3.22f1**（Resonite SDK / Renderite に合わせる）
- シーンをUnityの **Progressive** ライトマッパーでベイク済み（atlas・`uv2`・`lightmapScaleOffset` が生成されている）
- 影を落とす/受ける静的オブジェクトは **Contribute GI**（Static → Contribute Global Illumination）で、正しいライトマップUVを持つ
- マテリアルは**アルベド主体**（Standard系）だと乗算合成が正しく読める

---

## 既にベイク済みなら、そのまま持ってこれる

**既にライトベイク済みで、見た目が望み通り**のUnityシーンなら、パネルもテストシーンも再ベイクも不要。SDK Managerを接続して **Send Current Scene** を押すだけ——変換器が既存のライトマップを自動で拾います。パネルは**ベイクを作る/整える**時だけ使うものです。

---

## インストール

本機能は公式SDKのフォークにあります。

- **フォーク:** `https://github.com/tanossy/Resonite.UnitySDK`（ブランチ `feature/baked-lightmap-import`）
- **本家:** `https://github.com/Yellow-Dog-Man/Resonite.UnitySDK`

1. このフォークのUnity SDKをベース（公式SDKと同じ使い方）にする、**または** `Assets/ResoniteSDK/ComponentConverters/Unity Core/Rendering/` 配下のライトマップ取り込みスクリプトを既存SDKにコピーする。
2. Unity 2022.3.22f1 で開く。
3. ライトマップ変換は **Send Current Scene** 時に自動で走る。追加設定なし。

> 本家にマージされれば、フォークは不要になります（公式SDKに同梱される）。

---

## 使い方

### 通常経路

1. Unityでシーンをベイク（下の推奨設定参照）。
2. **`Resonite SDK > Open Resonite SDK Manager`** を開き、接続（AutoDiscovery か手動ポート）。
3. **Send Current Scene** を押す。以上。

### Lightmap Pipeline パネル（任意）

メニュー：**`Resonite SDK > Lightmap Pipeline`**。最大の難所である「影の出るベイク」を作るための道具です。

- **Baker** — Unity Standard（Bakeryは表示されるが、未導入ならグレーアウト）
- **Quality Preset** — low / mid / high / custom（解像度・サンプル数）
- **Lighting**（Unity Standard時のみ）— つまみ5個：Ambient Brightness / Ambient Color / Shadow Strength / Sun Color / Sun Angle（仰角・方位）。Sun系はシーンで最も明るい有効なディレクショナルライトを対象にし、無ければグレーアウト。
- **Bake** — つまみの値をシーンに適用してからベイク。
- **Bake & Send** — ベイク後、自動でResoniteへ送信。

### 推奨ベイク設定（ここで皆ハマる）

スクリプト生成や新規追加のライトは **Shadow Type = None** がデフォルトで、光は焼けても**影が焼けません**。綺麗な接地影を安定して出す設定：

- ディレクショナルライト：**Baked**、**Shadow Type = Soft**、Shadow Strength = 1
- 太陽の**角度を低め（仰角35°くらい）**にすると影が長く明瞭になる
- **環境光**：Flat・中程度（0.12くらい）。暗すぎると太陽の当たらない面が真っ黒に潰れる
- **補助光は弱め**に。強い補助光は部屋を満たして影を消す
- **AO ON**：オブジェクトと床の接地に陰りを出す
- オブジェクトは面に**載せる**（埋めない）。埋まると接地影がジオメトリ内に隠れる
- 焼き込み対象は **Contribute GI** にする

---

## Bakery（任意）

[Bakery](https://assetstore.unity.com/packages/tools/level-design/bakery-gpu-lightmapper-122218) を所有する人向けの経路も用意（ライトマップUVを再アトラス化し、メッシュによっては綺麗なチャートになる）。**Bakeryは必須ではありません**——Unity標準経路だけで完結します。Bakery非導入時はBakery関連コードは全てコンパイル対象外になり、パネルではBaker選択肢がグレーアウトで見える（導入すれば使えると分かる）ようにしています。

---

## フォークと本家

Yellow-Dog-Man SDK のフィーチャーブランチです。本家に追従しつつ、適切な形でライトマップ取り込み変換器を本家へ提案し、フォーク無しで誰でも使えるようにすることを目指します。IssueやPR歓迎。

---

## ライセンス

本リポジトリの**ベイクライトマップ取り込みの追加分**（ライトマップ変換器・デコーダ・マテリアルキャッシュ・Lightmap Pipeline エディタツール）は **MIT ライセンス**で公開します（英語版のライセンス全文参照）。

> **注意**：ベースの **Resonite Unity SDK** および **Bakery** は、それぞれ独自のライセンスに従います。このMIT許諾は追加分のみを対象とし、ベースSDKや第三者アセットは含みません。Bakeryは有料アセットであり、ここでは一切再配布しません。

---

## 免責

Resonite Unity SDK はベータで、全コンポーネントを変換できるわけではありません。本ツールはベイクライティングとリフレクションプローブを対象とし、複雑な本番シーンでの結果は変動します。大きな一括処理の前は必ずUnityプロジェクトのバックアップを取ってください。
