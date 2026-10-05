using Elements.Core;
using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

[Serializable]
public class SkyboxConverter
{
    public const string SKYBOX_ROOT_NAME = "__UnitySkybox";

    public GameObject SkyboxRoot;

    public ResRef<FrooxEngine.Skybox> Skybox;
    public ResRef<FrooxEngine.AmbientLightSH2> AmbientLight;
    public ResRef<FrooxEngine.ReflectionProbe> ReflectionProbe;
    public ResRef<FrooxEngine.ReflectionProbeSH2> ReflectionProbeSH2;
    public ResRef<FrooxEngine.ValueCopySH_L2> ValueCopy;

    // Originally disabled because Unity could not serialize the generated wrapper components that
    // held Sync<UnityEngine.Rendering.SphericalHarmonicsL2>, and the [NonSerialized] workaround on
    // those fields went with them. Upstream v1.1.2 replaced the wrappers with ResRef<T> containers
    // and fixed their serialization, so that specific blocker is gone - but SH2 sky ambient has
    // not been re-verified on the live client against the new architecture, and the baked-lightmap
    // path doesn't need it, so it stays off until someone checks it. The skybox material and the
    // reflection probe are still converted either way.
    public static bool ConvertSphericalHarmonics = false;

    public void EnsureRoot()
    {
        if (SkyboxRoot != null)
            return;

        // Try to get the root from the current components if they exist.
        //
        // 2026-07-27 bug fix (discovered on the live client), kept through the v1.1.2 merge: when
        // a container is in Unity's "fake null" state (the native side has been destroyed but the
        // C# reference itself is not null), `?.` (C#'s null-conditional operator) doesn't go
        // through Unity's operator== overload, so the `.gameObject` access throws a
        // MissingReferenceException. Confirmed to occur, for example, when
        // CleanupAssetConversionRoots() destroys __UnitySkybox entirely and these fields then get
        // restored via a domain reload. Upstream's one-liner here is
        // `Skybox.Container?.gameObject ?? ...`, which reintroduces exactly that crash, so the
        // explicit Unity-aware `!= null` checks are kept - now against the ResRef containers.
        if (Skybox.Container != null)
            SkyboxRoot = Skybox.Container.gameObject;
        else if (AmbientLight.Container != null)
            SkyboxRoot = AmbientLight.Container.gameObject;
        else if (ReflectionProbe.Container != null)
            SkyboxRoot = ReflectionProbe.Container.gameObject;

        if(SkyboxRoot == null)
        {
            // Try to find it in the scene
            var roots = SceneManager.GetActiveScene().GetRootGameObjects();
            SkyboxRoot = roots.FirstOrDefault(r => r.name == SKYBOX_ROOT_NAME);
        }

        // All failed, make one
        if(SkyboxRoot == null)
            SkyboxRoot = new GameObject(SKYBOX_ROOT_NAME);
    }

    // TODO!!! Handle different types of ambient light for conversion
    // Right now this just assumes that reflections and ambient light all come from the skybox
    public void ConvertCurrentSkybox(IConversionContext context)
    {
        EnsureComponent(ref Skybox);
        EnsureComponent(ref ReflectionProbe);

        if (!ConvertSphericalHarmonics)
        {
            RemoveComponent(ref AmbientLight);
            RemoveComponent(ref ReflectionProbeSH2);
            RemoveComponent(ref ValueCopy);
        }
        else
        {
            EnsureComponent(ref AmbientLight);
            EnsureComponent(ref ReflectionProbeSH2);
            EnsureComponent(ref ValueCopy);
        }

        // Setup the skybox material itself
        var skyboxMaterial = context.GetMaterial(RenderSettings.skybox);
        Skybox.Binding.Material = skyboxMaterial;

        // Setup reflection probe for the skybox
        // This is used for specular reflections and also to auto-calculate the SH2
        ReflectionProbe.Binding.SkyboxOnly = true;
        ReflectionProbe.Binding.BoxSize = Vector3.one * 1000000;
        ReflectionProbe.Binding.ClearFlags = Renderite.Shared.ReflectionProbeClear.Skybox;
        ReflectionProbe.Binding.HDR = true;
        ReflectionProbe.Binding.ProbeType = Renderite.Shared.ReflectionProbeType.OnChanges;
        ReflectionProbe.Binding.Intensity = 1f;

        while (ReflectionProbe.Binding.ChangesSources.Count < 2)
            ReflectionProbe.Binding.ChangesSources.Add();

        ReflectionProbe.Binding.ChangesSources[0] = Skybox.Binding;
        ReflectionProbe.Binding.ChangesSources[1] = skyboxMaterial;

        if (!ConvertSphericalHarmonics)
            return;

        // Assign the reflection probe as source for SH2 computation
        ReflectionProbeSH2.Binding.Probe = ReflectionProbe.Binding;

        // This should make it look roughly the same as Unity's own calculation
        ReflectionProbeSH2.Binding.Order0Scale = 1.5f;
        ReflectionProbeSH2.Binding.Order1Scale = 0.5f;
        ReflectionProbeSH2.Binding.Order2Scale = 0.5f;

        // Copy the value from the SH2 to AmbientLight
        ValueCopy.Binding.Source = ReflectionProbeSH2.Binding.AmbientLight_Element.Member;
        ValueCopy.Binding.Target = AmbientLight.Binding.AmbientLight_Element.Member;
    }

    void EnsureComponent<T>(ref ResRef<T> component)
        where T : ResoniteObject, FrooxEngine.IWorldElement, new()
    {
        if (component != null)
            return;

        component = SkyboxRoot.GetResoniteComponent<T>();

        if (component == null)
            component = SkyboxRoot.AddResoniteComponent<T>();
    }

    // ResRef is a struct, so "absent" is `default` (a null Container), not a null reference.
    void RemoveComponent<T>(ref ResRef<T> component)
        where T : ResoniteObject, FrooxEngine.IWorldElement, new()
    {
        if (component == null && SkyboxRoot != null)
            component = SkyboxRoot.GetResoniteComponent<T>();

        if (component == null)
            return;

        UnityEngine.Object.DestroyImmediate(component.Container);
        component = default;
    }
}
