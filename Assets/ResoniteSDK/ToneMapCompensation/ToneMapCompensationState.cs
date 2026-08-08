// 2026-08-08: マテリアル色へのトーンマップ近似(ColorGradingApproximation経由)とReflection Probe
// 強度減衰(ReflectionProbeConverter経由)を、パネル上でオン/オフできるようにするための
// プロセス全体で共有される設定。ConversionPassState.cs(SDK既存)と同じ「シンプルなstaticクラス」
// パターンに倣っている——IConversionContextインターフェースはResonite.UnitySDK.Bindings側の
// コンパイル済み型でこちら側から拡張できないため、この方式を採用した。
//
// ResoniteLinkWindow.OnGUI()がトグルチェックボックスの値を毎フレームここへ書き込む。
public static class ToneMapCompensationState
{
    public static bool Enabled = true;
}
