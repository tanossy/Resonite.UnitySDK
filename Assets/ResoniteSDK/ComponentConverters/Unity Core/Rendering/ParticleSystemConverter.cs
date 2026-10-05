using FrooxEngine.PhotonDust;
using UnityEngine;
using ParticleSystem = FrooxEngine.PhotonDust.ParticleSystem;

public static class EmitterHelper
{
    public static void SetFrom(this FrooxEngine.PhotonDust.ParticleEmitter emitter,
        FrooxEngine.PhotonDust.ParticleSystem system,
        UnityEngine.ParticleSystem.ShapeModule shape, UnityEngine.ParticleSystem.EmissionModule emission)
    {
        // emitter.Enabled gates whether the system emits particles at all — that's
        // EmissionModule.enabled in Unity, not ShapeModule.enabled (which only affects
        // emission position/direction; a disabled shape still emits from a point).
        emitter.Enabled = emission.enabled;
        emitter.System = system;

        // TODO!!! Handle other styles?
        emitter.Rate = emission.rateOverTime.constant;
    }
}

public class ParticleSystemConverter : ResoniteComponentConverter<UnityEngine.ParticleSystem>
{
    public ResRef<ParticleSystem> ParticleSystem;
    public ResRef<ParticleStyle> ParticleStyle;

    public ResRef<PositionSimulatorModule> PositionSimulator;
    public ResRef<LifetimeRangeInitializer> LifetimeInitializer;
    public ResRef<SizeRangeInitializer> SizeRangeInitializer;
    public ResRef<ColorRangeInitializer> ColorRangeInitializer;
    public ResRef<SpeedRangeInitializer> SpeedRangeInitializer;

    // Optional "over lifetime" modules — only present when Unity's matching module is enabled
    public ResRef<ColorOverLifetimeStartEnd> ColorOverLifetime;
    public ResRef<SizeOverLifetimeStartEnd> SizeOverLifetime;
    public ResRef<TextureSheetAnimator> TextureSheetAnimator;

    public ResRef<BillboardParticleRenderer> BillboardRenderer;
    public ResRef<MeshParticleRenderer> MeshRenderer;

    // Emitters
    public ResRef<BoxEmitter> BoxEmitter;
    public ResRef<SphereEmitter> SphereEmitter;
    public ResRef<ConeEmitter> ConeEmitter;
    public ResRef<MeshEmitter> MeshEmitter;
    public ResRef<SkinnedMeshEmitter> SkinnedMeshEmitter;
    public ResRef<CircleEmitter> CircleEmitter;

    // Modules

    TModule EnsureModule<TModule>(ref ResRef<TModule> wrapper)
        where TModule : ResoniteObject, IParticleSystemSubsystem, FrooxEngine.IWorldElement, new()
    {
        var style = ParticleStyle.Binding;

        return EnsureComponent<TModule>(ref wrapper, module => style.Modules.Add(module));
    }

    // Like EnsureModule, but for modules that only exist when the corresponding Unity
    // module is enabled — removes the module when it's been switched off. Returns null
    // (the module was removed / never created) when disabled.
    // ResRef is a struct, so "no module" is `default` (a null Container), not a null reference.
    TModule EnsureOptionalModule<TModule>(ref ResRef<TModule> module, bool enabled)
        where TModule : ResoniteObject, IParticleSystemSubsystem, FrooxEngine.IWorldElement, new()
    {
        if (!enabled)
        {
            if (module != null)
            {
                DestroyImmediate(module.Container);
                module = default;
            }
            return null;
        }

        return EnsureModule(ref module);
    }

    TEmitter EnsureEmitter<TEmitter>(ref ResRef<TEmitter> emitter)
        where TEmitter : ParticleEmitter, FrooxEngine.IWorldElement, new()
    {
        if (emitter == null)
        {
            // Remove any previous emitters
            CleanupEmitters();

            emitter = gameObject.AddResoniteComponent<TEmitter>();

            // Assign the system
            emitter.Binding.System = ParticleSystem.Binding;
        }

        return emitter.Binding;
    }

    protected override void UpdateConversion(UnityEngine.ParticleSystem target, IConversionContext context)
    {
        var system = EnsureComponent<FrooxEngine.PhotonDust.ParticleSystem>(ref ParticleSystem);
        var style = EnsureComponent<FrooxEngine.PhotonDust.ParticleStyle>(ref ParticleStyle,
            s => system.Style = s);

        var lifetime = EnsureModule<LifetimeRangeInitializer>(ref LifetimeInitializer);
        var size = EnsureModule<SizeRangeInitializer>(ref SizeRangeInitializer);
        var color = EnsureModule<ColorRangeInitializer>(ref ColorRangeInitializer);
        var speed = EnsureModule<SpeedRangeInitializer>(ref SpeedRangeInitializer);

        var position = EnsureModule<PositionSimulatorModule>(ref PositionSimulator);

        system.Enabled = true;
        system.persistent = true;

        var main = target.main;
        var renderer = target.gameObject.GetComponent<ParticleSystemRenderer>();
        var emission = target.emission;
        var shape = target.shape;

        system.MaxParticleCount = main.maxParticles;
        system.Style = style;

        // NOTE: SimulationSpace intentionally left untouched (Default = WorldRoot).
        // Emission position comes from the emitter's resolved world transform via normal
        // slot parenting, independent of this setting — two known-good native Resonite
        // fire assets (checked live: "Base Scene/VFX/Particles" and root "PhotonDust Fire")
        // both render correctly with SimulationSpace at its untouched WorldRoot default.
        // An earlier attempt to set LocalSpace/UseParentSpace here made the fire disappear
        // entirely (verified: reverted). If a real per-emitter simulation-space need turns
        // up later, re-derive it from a working example instead of guessing again.

        // Lifetime
        switch (main.startLifetime.mode)
        {
            case ParticleSystemCurveMode.Constant:
                lifetime.MinValue = lifetime.MaxValue = main.startLifetime.constant;
                break;

            case ParticleSystemCurveMode.TwoConstants:
                lifetime.MinValue = main.startLifetime.constantMin;
                lifetime.MaxValue = main.startLifetime.constantMax;
                break;
        }

        // Size
        switch (main.startSize.mode)
        {
            case ParticleSystemCurveMode.Constant:
                size.MinValue = size.MaxValue = main.startSize.constant * Vector3.one;
                break;

            case ParticleSystemCurveMode.TwoConstants:
                size.MinValue = main.startSize.constantMin * Vector3.one;
                size.MaxValue = main.startSize.constantMax * Vector3.one;
                break;
        }

        // Speed
        switch (main.startSpeed.mode)
        {
            case ParticleSystemCurveMode.Constant:
                speed.MinValue = speed.MaxValue = main.startSpeed.constant;
                break;

            case ParticleSystemCurveMode.TwoConstants:
                speed.MinValue = main.startSpeed.constantMin;
                speed.MaxValue = main.startSpeed.constantMax;
                break;
        }

        // Color
        switch (main.startColor.mode)
        {
            case ParticleSystemGradientMode.Color:
                color.MinValue = color.MaxValue = main.startColor.color.ToColorX_sRGB();
                break;

            case ParticleSystemGradientMode.TwoColors:
                color.MinValue = main.startColor.colorMin.ToColorX_sRGB();
                color.MaxValue = main.startColor.colorMax.ToColorX_sRGB();
                break;
        }

        // Color over lifetime. PhotonDust's StartEnd variant only samples two points, so we
        // evaluate Unity's gradient (which merges separate color/alpha key arrays) at t=0 and t=1 —
        // this doesn't preserve interior gradient stops, but is a large improvement over a flat color.
        var colorOverLifetime = target.colorOverLifetime;
        var colorOverLifetimeModule = EnsureOptionalModule<ColorOverLifetimeStartEnd>(
            ref ColorOverLifetime, colorOverLifetime.enabled);

        if (colorOverLifetimeModule != null && colorOverLifetime.color.mode == ParticleSystemGradientMode.Gradient)
        {
            var gradient = colorOverLifetime.color.gradient;
            colorOverLifetimeModule.StartColor = gradient.Evaluate(0f).ToColorX_sRGB();
            colorOverLifetimeModule.EndColor = gradient.Evaluate(1f).ToColorX_sRGB();
        }

        // Size over lifetime. Unity's curve is a *multiplier* on the particle's start size,
        // not an absolute value — bake the base size in so PhotonDust's absolute StartSize/EndSize
        // match what Unity would actually render.
        var sizeOverLifetime = target.sizeOverLifetime;
        var sizeOverLifetimeModule = EnsureOptionalModule<SizeOverLifetimeStartEnd>(
            ref SizeOverLifetime, sizeOverLifetime.enabled);

        if (sizeOverLifetimeModule != null && sizeOverLifetime.size.mode == ParticleSystemCurveMode.Curve)
        {
            var curve = sizeOverLifetime.size.curve;
            var baseSize = size.MaxValue; // already resolved above (constant or TwoConstants max)
            sizeOverLifetimeModule.StartSize = baseSize * curve.Evaluate(0f);
            sizeOverLifetimeModule.EndSize = baseSize * curve.Evaluate(1f);
        }

        // Texture sheet animation (flipbook). Fire/smoke effects commonly animate through a
        // grid of frames on the same texture instead of using a single static image.
        var textureSheetAnimation = target.textureSheetAnimation;
        var textureSheetAnimationModule = EnsureOptionalModule<TextureSheetAnimator>(
            ref TextureSheetAnimator, textureSheetAnimation.enabled);

        if (textureSheetAnimationModule != null)
        {
            textureSheetAnimationModule.TileGridSize = new Vector2Int(textureSheetAnimation.numTilesX, textureSheetAnimation.numTilesY);
            textureSheetAnimationModule.AnimationCycleCount = textureSheetAnimation.cycleCount;
            textureSheetAnimationModule.AnimationType = textureSheetAnimation.animation == UnityEngine.ParticleSystemAnimationType.WholeSheet
                ? PhotonDust.TextureSheetAnimationType.WholeSheet
                : PhotonDust.TextureSheetAnimationType.SingleRow;
        }

        // 2026-09-08: the renderer arm below calls context.GetMaterial(), which schedules a
        // material + texture upload. MeshRendererConverter guards the same call with
        // ShouldConvertMaterials; this one did not, so a Meshes Only pass queued a particle
        // texture upload whose StaticTexture2D component the pass filter had already dropped -
        // "Component with ID ... not found" on the provider update. Same guard here.
        if (!ConversionPassState.ShouldConvertMaterials)
            return;

        switch (renderer.renderMode)
        {
            case ParticleSystemRenderMode.Billboard:
                if (BillboardRenderer == null)
                {
                    CleanupRenderers();
                    BillboardRenderer = gameObject.AddResoniteComponent<BillboardParticleRenderer>();
                    system.Style.Renderer = BillboardRenderer.Binding;
                }

                var billboard = BillboardRenderer.Binding;
                var provider = context.GetMaterial(renderer.sharedMaterial);

                billboard.Material = provider;
                billboard.MinBillboardScreenSize = renderer.minParticleSize;
                billboard.MaxBillboardScreenSize = renderer.maxParticleSize;

                billboard.Alignment = Renderite.Shared.BillboardAlignment.Facing;

                break;

            case ParticleSystemRenderMode.Mesh:
                if (MeshRenderer == null)
                {
                    CleanupRenderers();
                    MeshRenderer = gameObject.AddResoniteComponent<MeshParticleRenderer>();
                    system.Style.Renderer = MeshRenderer.Binding;
                }

                var mesh = MeshRenderer.Binding;

                mesh.Material = context.GetMaterial(renderer.sharedMaterial);
                mesh.Mesh = context.GetMesh(renderer.mesh);

                break;
        }

        switch (shape.shapeType)
        {
            case ParticleSystemShapeType.Sphere:
                var sphere = EnsureEmitter(ref SphereEmitter);

                sphere.SetFrom(system, shape, emission);

                sphere.Radius = shape.radius;
                break;

            // BoxShell/BoxEdge weren't previously matched by this switch at all (silently no
            // emitter created for assets using them) — the EmitFromShell line below already
            // assumed it could see BoxShell, but that was unreachable dead code since the case
            // label above only matched plain Box. Same bug class as Cone (see above).
            case ParticleSystemShapeType.Box:
            case ParticleSystemShapeType.BoxShell:
            case ParticleSystemShapeType.BoxEdge:
                var box = EnsureEmitter(ref BoxEmitter);

                box.SetFrom(system, shape, emission);

                box.Size = shape.scale;

                box.EmitFromShell = shape.shapeType == ParticleSystemShapeType.BoxShell;

                box.Color0 = Color.white.ToColorX_sRGB();
                box.Color1 = Color.white.ToColorX_sRGB();
                box.Color2 = Color.white.ToColorX_sRGB();
                box.Color3 = Color.white.ToColorX_sRGB();
                box.Color4 = Color.white.ToColorX_sRGB();
                box.Color5 = Color.white.ToColorX_sRGB();
                box.Color6 = Color.white.ToColorX_sRGB();
                box.Color7 = Color.white.ToColorX_sRGB();
                break;

            case ParticleSystemShapeType.Circle:
                var circle = EnsureEmitter(ref CircleEmitter);

                circle.SetFrom(system, shape, emission);

                circle.Radius = shape.radius;
                circle.Scale = Vector2.one;
                break;

            // Unity has 4 distinct Cone variants (verified via ps.shape.shapeType, raw enum
            // values Cone=4/ConeShell=7/ConeVolume=8/ConeVolumeShell=9). Only the two "Volume"
            // variants actually spawn particles distributed along the cone's length; plain
            // Cone/ConeShell emit from the flat base disc only, and `length` there merely
            // shapes the velocity spread (the base is treated as if `length` units in front of
            // an implicit apex — narrower length = wider effective spread for the same angle).
            // Previously `Height` was set to `shape.length` unconditionally, which for a plain
            // Cone (this asset's actual type) turned a point-like base emission into particles
            // scattered across a `length`-meter-tall volume (a real Unity asset had length=5,
            // i.e. a 5-METER spawn volume for a candle flame) — this was the root cause of the
            // original "particles stretched across the room" bug, confirmed against
            // wiki.resonite.com/Component:ConeEmitter (its Japanese-language wiki text reads
            // "Height = 放出元のconeの高さ", i.e. "Height = the height of the emission-source
            // cone" — Height is literally the emission volume's height, not a Unity-length
            // passthrough).
            case ParticleSystemShapeType.Cone:
            case ParticleSystemShapeType.ConeShell:
            case ParticleSystemShapeType.ConeVolume:
            case ParticleSystemShapeType.ConeVolumeShell:
                var cone = EnsureEmitter(ref ConeEmitter);

                cone.SetFrom(system, shape, emission);

                cone.BaseRadius = shape.radius;

                bool isVolumeCone = shape.shapeType == ParticleSystemShapeType.ConeVolume
                    || shape.shapeType == ParticleSystemShapeType.ConeVolumeShell;
                cone.Height = isVolumeCone ? shape.length : 0f;

                cone.EmitFromShell = shape.shapeType == ParticleSystemShapeType.ConeShell
                    || shape.shapeType == ParticleSystemShapeType.ConeVolumeShell;
                break;

            case ParticleSystemShapeType.Mesh:
                var mesh = EnsureEmitter(ref MeshEmitter);

                mesh.SetFrom(system, shape, emission);

                mesh.Mesh = context.GetMesh(shape.mesh);
                mesh.UniformDistribution = true;

                switch(shape.meshShapeType)
                {
                    case ParticleSystemMeshShapeType.Vertex: mesh.EmitFrom = PhotonDust.MeshEmissionSource.Vertices; break;
                    case ParticleSystemMeshShapeType.Edge: mesh.EmitFrom = PhotonDust.MeshEmissionSource.Edges; break;
                    case ParticleSystemMeshShapeType.Triangle: mesh.EmitFrom = PhotonDust.MeshEmissionSource.Faces; break;
                }
                break;

            case ParticleSystemShapeType.SkinnedMeshRenderer:
                var skin = EnsureEmitter(ref SkinnedMeshEmitter);

                skin.SetFrom(system, shape, emission);

                switch (shape.meshShapeType)
                {
                    case ParticleSystemMeshShapeType.Vertex: skin.EmitFrom = PhotonDust.MeshEmissionSource.Vertices; break;
                    case ParticleSystemMeshShapeType.Edge: skin.EmitFrom = PhotonDust.MeshEmissionSource.Edges; break;
                    case ParticleSystemMeshShapeType.Triangle: skin.EmitFrom = PhotonDust.MeshEmissionSource.Faces; break;
                }

                // TODO!!!
                //skin.Skin = shape.skinnedMeshRenderer;
                break;
        }

        CleanupRemovedModules();
    }

    void CleanupRemovedModules()
    {
        ParticleStyle.Binding.Modules.Data.RemoveAll(m => m.Data == null);
    }

    void CleanupRenderers()
    {
        if (BillboardRenderer != null)
            DestroyImmediate(BillboardRenderer);

        if (MeshRenderer != null)
            DestroyImmediate(MeshRenderer);
    }

    void CleanupEmitters()
    {
        if (BoxEmitter != null)
            DestroyImmediate(BoxEmitter);

        if (SphereEmitter != null)
            DestroyImmediate(SphereEmitter);

        if (ConeEmitter != null)
            DestroyImmediate(ConeEmitter);

        if (MeshEmitter != null)
            DestroyImmediate(MeshEmitter);

        if (SkinnedMeshEmitter != null)
            DestroyImmediate(SkinnedMeshEmitter);

        if (CircleEmitter != null)
            DestroyImmediate(CircleEmitter);
    }

    // 2026-08-27: guarded on ExplicitCleanupRequested (see ResoniteComponentConverter.cs) so
    // this doesn't redundantly re-destroy the renderer/emitter wrappers when the whole
    // GameObject is already being torn down as a unit (e.g. Bakery's scene-setup restore
    // during a bake).
    protected override void Cleanup()
    {
        if (!ExplicitCleanupRequested)
            return;

        CleanupRenderers();
        CleanupEmitters();

        // 2026-09-08: CleanupRenderers/CleanupEmitters only cover the renderer and emitter
        // wrappers. The ParticleSystem, ParticleStyle and every module wrapper survived, so
        // each converter rebuild (ResetConversionState -> CleanupConverters, i.e. every new
        // ResoniteLink session) left a full set behind and the next Initialize() added another
        // - 78 components on one particle GameObject were measured. The orphaned ParticleStyle
        // still referenced a destroyed renderer, that reference went out in the batch, Resonite
        // answered with an unresolvable-reference failure carrying an id the client had already
        // retired, and ResoniteLink.dll closed its own socket over it. See PARTHENON-318.
        //
        // Reflected over this converter's own ResoniteComponent fields rather than listed by
        // hand, so a wrapper added later cannot be forgotten here again.
        DestroyOwnWrappers();
    }

    /// <summary>Destroys every Resonite component container this converter holds a field reference to.</summary>
    void DestroyOwnWrappers()
    {
        var fields = GetType().GetFields(System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance);

        foreach (var field in fields)
        {
            // Upstream v1.1.2 moved every one of this converter's component fields from a
            // generated wrapper MonoBehaviour to a ResRef<T> STRUCT that merely holds the
            // container. A plain `typeof(ResoniteComponent).IsAssignableFrom(field.FieldType)`
            // test no longer matches any of them, which would have turned this whole sweep into
            // a silent no-op and brought PARTHENON-318's disconnect straight back. Both field
            // shapes are handled, so the "can't forget a field added later" property survives.
            bool isResRef = field.FieldType.IsGenericType
                && field.FieldType.GetGenericTypeDefinition() == typeof(ResRef<>);

            if (!isResRef && !typeof(ResoniteComponent).IsAssignableFrom(field.FieldType))
                continue;

            var value = field.GetValue(this);

            var container = isResRef
                ? field.FieldType.GetProperty(nameof(ResRef<FrooxEngine.PhotonDust.ParticleSystem>.Container))
                    ?.GetValue(value) as ResoniteComponent
                : value as ResoniteComponent;

            if (container == null)
                continue;

            DestroyImmediate(container);

            // `null` is not assignable to a struct field - the empty ResRef is its default value.
            field.SetValue(this, isResRef ? System.Activator.CreateInstance(field.FieldType) : null);
        }
    }
}