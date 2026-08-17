public enum ResoniteSdkConversionPass
{
    Full,
    MeshesOnly,
    MaterialsOnly,
    LightmapsOnly,
}

public static class ConversionPassState
{
    public static ResoniteSdkConversionPass ActivePass { get; set; } = ResoniteSdkConversionPass.Full;

    // 2026-08-18: moved here from ResoniteLinkWindow (was a plain field on the main SDK panel,
    // read via SceneConverter's `_window.ForceRefreshGeneratedLightmaps`) so the toggle can live
    // in the Lightmap Pipeline panel instead, keeping the main panel limited to what's actually
    // part of vanilla Resonite.UnitySDK. Same decoupling pattern already used by
    // ToneMapCompensationState.
    public static bool ForceRefreshGeneratedLightmaps { get; set; } = true;

    public static bool ShouldConvertMeshes =>
        ActivePass != ResoniteSdkConversionPass.MaterialsOnly &&
        ActivePass != ResoniteSdkConversionPass.LightmapsOnly;

    public static bool ShouldConvertMaterials => ActivePass != ResoniteSdkConversionPass.MeshesOnly;

    public static bool ShouldUploadSourceMaterialTextures =>
        ActivePass != ResoniteSdkConversionPass.LightmapsOnly;
}
