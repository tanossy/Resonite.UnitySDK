// 2026-08-08: Process-wide shared setting that lets the panel toggle the material color tonemap
// approximation (via ColorGradingApproximation) and the Reflection Probe intensity attenuation (via
// ReflectionProbeConverter) on and off. This follows the same "simple static class" pattern as the
// existing ConversionPassState.cs (SDK-provided) — since the IConversionContext interface is a
// precompiled type on the Resonite.UnitySDK.Bindings side that cannot be extended from here, this
// approach was chosen instead.
//
// ResoniteLinkWindow.OnGUI() writes the toggle checkbox's value here every frame.
public static class ToneMapCompensationState
{
    public static bool Enabled = true;
}
