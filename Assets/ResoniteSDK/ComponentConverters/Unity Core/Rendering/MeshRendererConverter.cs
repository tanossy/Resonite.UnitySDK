using System.Collections.Generic;
using UnityEngine;

public static class MeshRendererHelper
{
    public static void SetFrom(this FrooxEngine.MeshRenderer resonite, UnityEngine.MeshRenderer unity, IConversionContext context)
    {
        // We need to explicitly cast here to use the shared method for setting from the Renderer itself.
        // allowLightmap: true - this is the actual MeshRenderer entry point (as opposed to the
        // shared Renderer-level helper below, which SkinnedMeshRendererConverter also funnels
        // through), so lightmap-variant substitution is allowed here.
        resonite.SetFrom((UnityEngine.Renderer)unity, context, allowLightmap: true);

        // We need to get the matching mesh filter for this to get the mesh data
        var meshFilter = unity.transform.GetComponent<MeshFilter>();

        if (meshFilter == null)
            resonite.Mesh = null;
        else
            resonite.Mesh = context.GetMesh(meshFilter.sharedMesh);
    }

    // allowLightmap defaults to false so that SkinnedMeshRendererHelper.SetFrom (which calls this
    // same shared helper for everything except mesh/skinning data) never routes materials through
    // LightmapMaterialCache - skinned meshes are not lightmapped by Unity's baked GI system, and
    // the marker-shader/variant substitution path below is only meaningful for static MeshRenderer
    // slots. Only MeshRendererHelper.SetFrom(MeshRenderer, ...) above opts in explicitly.
    public static void SetFrom(this FrooxEngine.MeshRenderer resonite, UnityEngine.Renderer unity, IConversionContext context, bool allowLightmap = false)
    {
        // There's no base to set these from, so we can't use the shared one?
        resonite.persistent = true;
        resonite.Enabled = unity.enabled;

        switch (unity.shadowCastingMode)
        {
            case UnityEngine.Rendering.ShadowCastingMode.On:
                resonite.ShadowCastMode = Renderite.Shared.ShadowCastMode.On;
                break;

            case UnityEngine.Rendering.ShadowCastingMode.Off:
                resonite.ShadowCastMode = Renderite.Shared.ShadowCastMode.Off;
                break;

            case UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly:
                resonite.ShadowCastMode = Renderite.Shared.ShadowCastMode.ShadowOnly;
                break;

            case UnityEngine.Rendering.ShadowCastingMode.TwoSided:
                resonite.ShadowCastMode = Renderite.Shared.ShadowCastMode.DoubleSided;
                break;
        }

        switch (unity.motionVectorGenerationMode)
        {
            case MotionVectorGenerationMode.Object:
                resonite.MotionVectorMode = Renderite.Shared.MotionVectorMode.Object;
                break;

            case MotionVectorGenerationMode.Camera:
                resonite.MotionVectorMode = Renderite.Shared.MotionVectorMode.Camera;
                break;

            case MotionVectorGenerationMode.ForceNoMotion:
                resonite.MotionVectorMode = Renderite.Shared.MotionVectorMode.NoMotion;
                break;
        }

        resonite.SortingOrder = unity.sortingOrder;

        // Convert materials!
        var sourceMaterials = unity.sharedMaterials;

        // Tracks slots that were NOT eligible for lightmap-variant substitution (non-Standard
        // shader, non-Opaque mode, etc. - see LightmapMaterialCache), so we can warn once if a
        // renderer ends up with a split outcome (some slots lightmapped, some not) rather than
        // silently importing a patchwork of lit/unlit materials.
        List<string> nonEligibleMaterialNames = null;

        for (int i = 0; i < sourceMaterials.Length; i++)
        {
            var originalMat = sourceMaterials[i];
            var mat = originalMat;

            // Empty material slots (originalMat == null) are a normal, common Unity authoring
            // pattern (e.g. a renderer with fewer assigned materials than submeshes) - they were
            // never eligible for lightmap-variant substitution to begin with
            // (LightmapMaterialCache.GetVariantOrOriginal already just returns null-for-null
            // unchanged), so counting them as "non-eligible" here was a false warning, not a real
            // one (Loki 2nd-pass review 指摘3). Skip both the substitution call and the
            // eligibility bookkeeping for them entirely; mat stays null exactly as it always did
            // for a null slot, so context.GetMaterial(mat) below sees the identical null it always
            // has.
            if (allowLightmap && originalMat != null)
            {
                mat = LightmapMaterialCache.GetVariantOrOriginal(unity, originalMat);

                if (ReferenceEquals(mat, originalMat))
                {
                    nonEligibleMaterialNames ??= new List<string>();
                    nonEligibleMaterialNames.Add(originalMat.name);
                }
            }

            var converted = context.GetMaterial(mat);

            if (resonite.Materials.Count == i)
                resonite.Materials.Add(converted);
            else
                resonite.Materials[i] = converted;
        }

        // Only warn on a *mixed* outcome (some slots lightmapped, some not) - a renderer with no
        // baked lightmap at all is the common case and would otherwise spam this on every single
        // non-lightmapped renderer in the scene.
        if (nonEligibleMaterialNames != null && nonEligibleMaterialNames.Count > 0 && nonEligibleMaterialNames.Count < sourceMaterials.Length)
        {
            Debug.LogWarning($"[ResoniteSDK] Renderer '{unity.name}': materials {string.Join(", ", nonEligibleMaterialNames)} " +
                "are not lightmap-eligible (non-Standard or non-Opaque); those slots will import without baked lighting.");
        }

        // Remove excess material slots
        while (resonite.Materials.Count > sourceMaterials.Length)
            resonite.Materials.RemoveAt(resonite.Materials.Count - 1);
    }
}

public class MeshRendererConverter : ResoniteSingleComponentConverter<MeshRenderer, FrooxEngine.MeshRendererWrapper>
{
    protected override void UpdateConversion(MeshRenderer target, IConversionContext context)
    {
        Binding.Data.SetFrom(target, context);
    }
}
