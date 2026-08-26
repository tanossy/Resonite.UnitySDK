using FrooxEngine;

public static class MeshColliderHelper
{
    public static void SetFrom(this FrooxEngine.MeshCollider resonite, UnityEngine.MeshCollider unity, IConversionContext context)
    {
        if (unity.convex)
            throw new System.InvalidOperationException($"Unity mesh collider is convex. You need to use ConvexHullCollider instead");

        // Set the base data
        resonite.SetFrom((UnityEngine.Collider)unity);

        if (ConversionPassState.ShouldConvertMeshes)
            resonite.Mesh = context.GetMesh(unity.sharedMesh);

        // Unity Mesh Colliders are one-sided based on their documentation:
        // https://docs.unity3d.com/6000.3/Documentation/Manual/mesh-colliders-introduction.html
        resonite.Sidedness = MeshColliderSidedness.Front;
    }

    public static void SetFrom(this FrooxEngine.ConvexHullCollider resonite, UnityEngine.MeshCollider unity, IConversionContext context)
    {
        if (!unity.convex)
            throw new System.InvalidOperationException($"Unity mesh collider is not convex. You need to use MeshCollider instead");

        // Set the base data
        resonite.SetFrom((UnityEngine.Collider)unity);

        if (ConversionPassState.ShouldConvertMeshes)
            resonite.Mesh = context.GetMesh(unity.sharedMesh);
    }
}

public class MeshColliderConverter : ResoniteComponentConverter<UnityEngine.MeshCollider>
{
    public MeshColliderWrapper MeshBinding;
    public ConvexHullColliderWrapper ConvexHullBinding;

    protected override void UpdateConversion(UnityEngine.MeshCollider target, IConversionContext context)
    {
        // Resonite represents Convex Hull & Mesh Colliders through separate components, rather than one with a toggle
        // We need to swith between them appropriately based on the flag whenever we update this.
        if(target.convex)
        {
            if (MeshBinding != null)
                DestroyImmediate(MeshBinding);

            if (ConvexHullBinding == null)
                ConvexHullBinding = gameObject.AddComponent<ConvexHullColliderWrapper>();

            ConvexHullBinding.Data.SetFrom(target, context);
        }
        else
        {
            if (ConvexHullBinding != null)
                DestroyImmediate(ConvexHullBinding);

            if (MeshBinding == null)
                MeshBinding = gameObject.AddComponent<MeshColliderWrapper>();

            MeshBinding.Data.SetFrom(target, context);
        }
    }

    // 2026-08-27: only explicitly destroys its wrapper components when the caller set
    // ExplicitCleanupRequested (see ResoniteComponentConverter.cs's field comment) - otherwise
    // this converter's whole GameObject is being destroyed as a unit, and doing so here too is
    // redundant (triggers Unity's "Destroying object multiple times" warning).
    protected override void Cleanup()
    {
        if (!ExplicitCleanupRequested)
            return;

        if (MeshBinding != null)
            DestroyImmediate(MeshBinding);

        if (ConvexHullBinding != null)
            DestroyImmediate(ConvexHullBinding);
    }
}
