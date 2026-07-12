public enum ResoniteSdkConversionPass
{
    Full,
    MeshesOnly,
    MaterialsOnly,
}

public static class ConversionPassState
{
    public static ResoniteSdkConversionPass ActivePass { get; set; } = ResoniteSdkConversionPass.Full;

    public static bool ShouldConvertMeshes => ActivePass != ResoniteSdkConversionPass.MaterialsOnly;

    public static bool ShouldConvertMaterials => ActivePass != ResoniteSdkConversionPass.MeshesOnly;
}
