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

    public static bool ShouldConvertMeshes =>
        ActivePass != ResoniteSdkConversionPass.MaterialsOnly &&
        ActivePass != ResoniteSdkConversionPass.LightmapsOnly;

    public static bool ShouldConvertMaterials => ActivePass != ResoniteSdkConversionPass.MeshesOnly;

    public static bool ShouldUploadSourceMaterialTextures =>
        ActivePass != ResoniteSdkConversionPass.LightmapsOnly;
}
