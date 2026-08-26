using System;
using UnityEngine;

[ExecuteInEditMode]
public abstract class ResoniteComponentConverter : MonoBehaviour
{
    [SerializeField]
    public Component Target;

    // 2026-08-27 (per Tanossy's feedback, after seeing dozens of "Destroying object multiple
    // times" warnings flood the console on every single Bakery bake): a converter and its
    // Resonite-side wrapper component(s) always live on the same GameObject as the original
    // Unity component (see SceneConverter.UpdateComponentConversions()'s
    // `root.gameObject.AddComponent(converterInfo.Type)`). Cleanup() used to unconditionally
    // DestroyImmediate() those wrapper components from OnDestroy() - correct when a caller
    // destroys just the converter component and the GameObject survives (the only two real
    // callers: SceneConverter.UpdateComponentConversions() when the Unity source component was
    // removed, and ResoniteLinkWindow.CleanupConverters() during a full reset), but redundant
    // whenever the whole GameObject is destroyed as a unit instead - e.g. deleting a Light in
    // the Hierarchy, or Bakery's RestoreSceneManagerSetup() tearing down its temporary bake
    // scene, which destroys every object in it (converters, wrappers, everything) in one
    // cascade. In that case the wrapper is already being destroyed by the same cascade, and
    // Cleanup() trying to destroy it again is exactly what triggers Unity's warning.
    //
    // Fix: only the two real explicit-destroy callers above set this flag right before their
    // DestroyImmediate(converter) call. Every other OnDestroy() (GameObject/scene teardown)
    // leaves it false, and Cleanup() implementations now skip their explicit wrapper-destroy
    // calls in that case, trusting Unity's own cascade to handle it - which it always does
    // correctly and silently on its own.
    public bool ExplicitCleanupRequested;

    public void Initialize(Component target)
    {
        Target = target;

        // Run any initialization code
        Initialize();
    }

    public abstract void UpdateConversion(IConversionContext context);

    protected abstract void Initialize();
    protected abstract void Cleanup();

    [ExecuteInEditMode]
    void OnDestroy() => Cleanup();
}

/// <summary>
/// This is the best class to derive from when you need versatility in how the component converts. 
/// </summary>
/// <typeparam name="T"></typeparam>
public abstract class ResoniteComponentConverter<T> : ResoniteComponentConverter
    where T : Component
{
    protected sealed override void Initialize() => Initialize((T)Target);
    public sealed override void UpdateConversion(IConversionContext context) => UpdateConversion((T)Target, context);

    protected virtual void Initialize(T target) {  }
    protected abstract void UpdateConversion(T target, IConversionContext context);

    protected TComponent EnsureComponent<TComponent, TWrapper>(ref TWrapper wrapper, 
        Action<TComponent> onAdded = null)
        where TWrapper : ResoniteComponent<TComponent>
        where TComponent : ResoniteObject, FrooxEngine.IWorldElement, new()
    {
        if (wrapper == null)
            wrapper = gameObject.AddComponent<TWrapper>();

        var data = wrapper.Data;

        onAdded?.Invoke(data);

        return data;
    }
}

/// <summary>
/// This provides convenient way to define conversions that map 1:1 Unity component to a Resonite component.
/// It automatically handles the instantiation and cleanup, so you only need to worry about providing the conversion update code.
/// </summary>
/// <typeparam name="TUnity"></typeparam>
/// <typeparam name="TResoniteWrapper"></typeparam>
public abstract class ResoniteSingleComponentConverter<TUnity, TResoniteWrapper> : ResoniteComponentConverter<TUnity>
    where TUnity : Component
    where TResoniteWrapper : ResoniteComponent
{
    public TResoniteWrapper Binding;

    protected override void Initialize(TUnity target)
    {
        base.Initialize(target);

        Binding = gameObject.AddComponent<TResoniteWrapper>();
    }

    protected override void Cleanup()
    {
        if (!ExplicitCleanupRequested)
            return;

        // Cleanup the binding if it still exists
        if (Binding == null)
            return;

        DestroyImmediate(Binding);
    }
}
