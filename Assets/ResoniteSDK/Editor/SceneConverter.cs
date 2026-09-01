using FrooxEngine;
using ResoniteLink;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

[Serializable]
public class SceneConverter : IConversionContext
{
    public string UniqueSessionId => _window.UniqueSessionId;
    public LinkInterface Link => _window.Link;

    public bool IsCorrupted { get; private set; }
    public bool IsRealtimeModeActive { get; private set; }

    public bool LogMessageJSON => _window.LogMessageJSON;
    public bool ConvertSkybox => _window.ConvertSkybox;
    public bool ForceRefreshGeneratedLightmaps => ConversionPassState.ForceRefreshGeneratedLightmaps;
    public ResoniteSdkConversionPass ActiveConversionPass => ConversionPassState.ActivePass;

    // TODO!!! Move this to a dedicated connection manager so the Window is only managing the UI?
    ResoniteLinkWindow _window;

    SkyboxConverter _skybox = new SkyboxConverter();

    [SerializeField]
    Dictionary<Transform, ResoniteLink.Slot> _transformMap = new Dictionary<Transform, ResoniteLink.Slot>();

    [SerializeField]
    Dictionary<ResoniteComponent, Transform> _existingComponents = new Dictionary<ResoniteComponent, Transform>();

    [SerializeField]
    HashSet<Transform> _existingSlots = new HashSet<Transform>();

    // 2026-07-14: Single intermediate parent slot. All Unity root objects are sent underneath
    // this slot rather than directly under the world Root (since this is a synthetic slot with
    // no real Unity Transform behind it, it can't be tracked via _transformMap, so it's held in
    // its own dedicated field instead).
    [SerializeField]
    ResoniteLink.Slot _importRootSlot;

    [SerializeField]
    bool _importRootSlotAdded;

    public const string ImportRootSlotName = "Unity Import";

    [SerializeField]
    Dictionary<IWorldElement, string> _elementToId = new Dictionary<IWorldElement, string>();

    [SerializeField]
    Dictionary<string, IWorldElement> _idToElement = new Dictionary<string, IWorldElement>();

    AssetConversionManager _assetConverter;

    Dictionary<UnityEngine.Component, List<Action>> _deferedActions = new Dictionary<UnityEngine.Component, List<Action>>();

    int _messageIndex;

    public string AllocateId(IWorldElement o = null)
    {
        if (o is FrooxEngine.Slot)
            throw new ArgumentException($"Cannot allocate ID for a Slot object! This needs to be handled through transforms");

        // 2026-08-08: ID generation source switched to GlobalIdAllocator (externalized into
        // Editor/GlobalIdAllocator.cs). See that file's comment for the rationale and the
        // real-world bug this fixes.
        return $"Unity_{UniqueSessionId}_{o?.GetType().Name}_{GlobalIdAllocator.Next():X}";
    }

    public string GetId(IWorldElement o)
    {
        if (o is null)
            return null;

        // We need to treat slots differently, because they map to transforms
        if (o is FrooxEngine.Slot slot)
            return GetTransformSlotId(slot.Transform);

        return _elementToId[o];
    }
    public string GetIdOrAllocate(IWorldElement o) => GetIdOrAllocate(o, out _);
    public string GetIdOrAllocate(IWorldElement o, out bool allocated)
    {
        if (o == null)
            throw new ArgumentNullException();

        // We need to treat slots differently, because they map to transforms
        if (o is FrooxEngine.Slot slot)
        {
            if (slot.Transform == null)
                throw new Exception($"Slot's transform reference is null! Is actually null: {slot.Transform is null}");

            allocated = false;
            return GetTransformSlotId(slot.Transform);
        }

        if (!_elementToId.TryGetValue(o, out var id))
        {
            id = AllocateId(o);
            _elementToId.Add(o, id);
            _idToElement.Add(id, o);

            allocated = true;
        }
        else
            allocated = false;

        return id;
    }
    public void RemoveId(IWorldElement o)
    {
        _idToElement.Remove(_elementToId[o]);
        _elementToId.Remove(o);
    }

    public string GetTransformSlotId(Transform transform) => GetLinkSlot(transform).ID;

    public string GetUniqueMessageId(string prefix) => $"{prefix}_{_messageIndex++}";

    public IWorldElement TryResolveId(string id)
    {
        if (_idToElement.TryGetValue(id, out var worldElement))
            return worldElement;

        return null;
    }

    #region ASSETS

    public FrooxEngine.IAssetProvider<FrooxEngine.Mesh> GetMesh(UnityEngine.Mesh mesh, AssetMessagePostProcessor postProcessor = null)
    {
        if (mesh == null)
            return null;

        return _assetConverter.GetMesh(mesh, postProcessor);
    }

    public FrooxEngine.IAssetProvider<FrooxEngine.ITexture2D> GetITexture2D(UnityEngine.Texture texture, AssetMessagePostProcessor postProcessor = null)
    {
        if (texture == null)
            return null;

        switch (texture)
        {
            case UnityEngine.Texture2D texture2D:
                return (FrooxEngine.IAssetProvider<FrooxEngine.ITexture2D>)GetTexture2D(texture2D, postProcessor);

            default:
                Debug.LogWarning($"Unsupported ITexture2D type: {texture.GetType()}");
                return null;
        }
    }

    public FrooxEngine.IAssetProvider<FrooxEngine.ITexture> GetITexture(UnityEngine.Texture texture, AssetMessagePostProcessor postProcessor = null)
    {
        if (texture == null)
            return null;

        switch (texture)
        {
            case UnityEngine.Texture2D texture2D:
                return (FrooxEngine.IAssetProvider<FrooxEngine.ITexture>)GetTexture2D(texture2D, postProcessor);

            case UnityEngine.Cubemap cubemap:
                return (FrooxEngine.IAssetProvider<FrooxEngine.ITexture>)GetCubemap(cubemap, postProcessor);

            default:
                Debug.LogWarning($"Unsupported ITexture2D type: {texture.GetType()}");
                return null;
        }
    }

    public FrooxEngine.IAssetProvider<FrooxEngine.Texture2D> GetTexture2D(UnityEngine.Texture2D texture, AssetMessagePostProcessor postProcessor = null)
    {
        if (texture == null)
            return null;

        return _assetConverter.GetTexture2D(texture, postProcessor);
    }

    public FrooxEngine.IAssetProvider<FrooxEngine.Cubemap> GetCubemap(UnityEngine.Cubemap cubemap, AssetMessagePostProcessor postProcessor = null)
    {
        if (cubemap == null)
            return null;

        return _assetConverter.GetCubemap(cubemap, postProcessor);
    }

    public IAssetProvider<FrooxEngine.Material> GetMaterial(UnityEngine.Material material)
    {
        if (material == null)
            return null;

        return _assetConverter.GetMaterial(material);
    }

    public IAssetProvider<FrooxEngine.AudioClip> GetAudioClip(UnityEngine.AudioClip audioClip, AssetMessagePostProcessor postProcessor = null)
    {
        if (audioClip == null)
            return null;

        return _assetConverter.GetAudioClip(audioClip, postProcessor);
    }

    public void EnsureAssetConverter()
    {
        if (_assetConverter != null)
            return;

        _assetConverter = new AssetConversionManager(this);
    }

    #endregion

    public void EnsureInitialized(ResoniteLinkWindow window)
    {
        _window = window;
    }

    public void StartRealtimeMode()
    {
        if (IsRealtimeModeActive)
            throw new InvalidOperationException("Realtime mode is already active");

        // We must convert the whole scene first
        ConvertScene();

        // Start listening to events
        ObjectChangeEvents.changesPublished += ObjectChangeEvents_changesPublished;

        IsRealtimeModeActive = true;
    }

    public void StopRealtimeMode()
    {
        if (!IsRealtimeModeActive)
            throw new InvalidOperationException("Realtime mode is not active");

        ObjectChangeEvents.changesPublished -= ObjectChangeEvents_changesPublished;

        IsRealtimeModeActive = false;
    }

    public void ConvertScene()
    {
        ConvertScene(ResoniteSdkConversionPass.Full);
    }

    public void ConvertMeshesOnly()
    {
        ConvertScene(ResoniteSdkConversionPass.MeshesOnly);
    }

    public void ConvertMaterialsOnly()
    {
        ConvertScene(ResoniteSdkConversionPass.MaterialsOnly);
    }

    public void ConvertLightmapsOnly()
    {
        ConvertScene(ResoniteSdkConversionPass.LightmapsOnly);
    }

    void ConvertScene(ResoniteSdkConversionPass pass)
    {
        // 2026-08-30: per-send memory hygiene (managed GC + asset GC with before/after logging) -
        // see ConversionMemoryHygiene.cs for the Editor.log evidence behind it.
        ConversionMemoryHygiene.BeforeSend($"before {pass} send");

        if (pass != ResoniteSdkConversionPass.MeshesOnly && ForceRefreshGeneratedLightmaps)
            LightmapMaterialCache.ClearGeneratedLightmapVariants();

        // Ensure asset converter has been initialized
        EnsureAssetConverter();

        if (pass == ResoniteSdkConversionPass.Full && ConvertSkybox)
            _skybox.EnsureRoot();

        // 2026-08-08: Exclusion logic moved to SceneRootFilter (externalized into
        // Editor/SceneRootFilter.cs).
        var roots = SceneManager.GetActiveScene().GetRootGameObjects()
            .Where(g => !SceneRootFilter.ShouldExclude(g));

        var previousPass = ConversionPassState.ActivePass;
        ConversionPassState.ActivePass = pass;
        try
        {
            Convert(roots.Select(g => g.transform));
        }
        finally
        {
            ConversionPassState.ActivePass = previousPass;
        }
    }

    public void RetryMissingAssetURLs()
    {
        try
        {
            EnsureAssetConverter();

            if (Link == null || !Link.IsConnected)
                throw new InvalidOperationException("ResoniteLink is not connected. Reconnect before retrying missing assets.");

            var scheduled = _assetConverter.ScheduleMissingAssetURLRetries();

            if (scheduled == 0)
            {
                Debug.Log("[ResoniteSDK] Retry Missing Asset URLs: no local StaticAssetProvider with a null URL was found.");
                return;
            }
            else
            {
                var messages = new List<DataModelOperation>();

                // 2026-07-14: This path calls Convert/ConvertHierarchy directly instead of
                // going through Convert(IEnumerable<Transform> roots), so if _importRootSlot is
                // left uninitialized, GatherTransformData's `_importRootSlot.ID` reference will
                // NPE. This call is idempotent, so it's safe to invoke every time.
                EnsureImportRootSlot(messages);

                Convert(_assetConverter.AssetsRoot, messages);

                foreach (var root in _assetConverter.UpdatedAssetProviderRoots)
                    ConvertHierarchy(root, messages);

                SendOperationBatch(messages);

                Debug.Log($"[ResoniteSDK] Retry Missing Asset URLs: retried {scheduled} missing asset provider(s).");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"FATAL ERROR while retrying missing asset URLs!\n{ex}");
            IsCorrupted = true;
        }
    }

    public void Convert(IEnumerable<Transform> roots)
    {
        try
        {
            _assetConverter.BeginConversion();

            if (ActiveConversionPass == ResoniteSdkConversionPass.Full && ConvertSkybox)
                _skybox.ConvertCurrentSkybox(this);

            // First update all component conversions
            foreach (var root in roots)
                UpdateComponentConversions(root);

            var messages = new List<DataModelOperation>();

            // 2026-07-14 bug fix (per Tanossy's feedback): previously, each of Unity's root
            // GameObjects (Lighting/Structure/__UnityAssets/__UnitySkybox, etc. - each an
            // independent Unity scene root) was sent directly as a child of the Resonite world's
            // Root (via GatherTransformData's `transform.parent == null -> TargetID = "Root"`).
            // This caused multiple disconnected clusters of converted content to pile up directly
            // under the world Root, and every time they got duplicated across sessions it became
            // hard to tell which cluster was our own output and clean it up. By introducing a
            // single intermediate parent slot (ImportRootSlotName) and grouping all Unity roots
            // underneath it, cleanup becomes as simple as deleting this one named slot.
            EnsureImportRootSlot(messages);

            foreach (var root in roots)
                ConvertHierarchy(root, messages);

            // Process any removals after all other stuff has been updated.
            // This way any transform that were reparented will be in new safe locations
            ProcessRemovals(messages);

            SendOperationBatch(messages);

            // 2026-08-26 (per Tanossy's direction, after 7 rounds of new C# Resonite.UnitySDK.
            // Bindings-specific bugs surfacing one per layer while building the "Light Tuning
            // Panel" via that SDK - see LightTuningPanelBuilder.cs's own header comment for the
            // full history): rather than build the panel's UIX/ProtoFlux graph through this SDK's
            // typed component wrappers, this now only writes out each light's baseline data
            // (slot id / intensity / color) and hands off to a separate, deterministic Python
            // script that reconstructs the panel via raw ResoniteLink JSON - the same technique
            // already proven (real server, real visible result) in the original 2026-08-24
            // hand-built version this whole feature is based on. Placed after SendOperationBatch
            // (not before) so this only runs once the room itself is confirmed sent - if
            // SendOperationBatch throws, the catch block below skips this entirely, matching the
            // old gating ("don't build the panel against content that may not exist yet").
            if (ActiveConversionPass == ResoniteSdkConversionPass.Full)
                WriteLightTuningPanelInputAndLaunchBuilder();

            // Post processing
            foreach (var root in roots)
                foreach (var postprocessor in root.GetComponentsInChildren<IConversionPostProcessor>())
                    postprocessor.PostProcessConversion(this);
        }
        catch (Exception ex)
        {
            Debug.LogError($"FATAL ERROR in conversion!" +
                $"\nThis is likely due to a bug in the SDK, please report this at: https://github.com/Yellow-Dog-Man/Resonite.UnitySDK/issues\n" +
                $"\n---------------------------------\n" +
                $"TECHNICAL INFO:\n{ex}");

            // Stop realtime mode if it's active
            if(IsRealtimeModeActive)
                StopRealtimeMode();

            // This conversion is now in corrupted state, we can't continue.
            // This will force reset
            IsCorrupted = true;
        }
    }

    void SendOperationBatch(List<DataModelOperation> messages)
    {
        if (Link == null || !Link.IsConnected)
            throw new InvalidOperationException("ResoniteLink is not connected. Reconnect before sending the scene.");

        // Only send messages when there are actually any
        // We still want to run the rest of the function, because there can be any asset conversions scheduled
        if (messages.Count > 0)
        {
            Task.Run(async () =>
            {
                // For debugging purposes
                if(LogMessageJSON)
                {
                    var operations = new DataModelOperationBatch();
                    operations.Operations = messages.ToList<Message>();
                    var json = System.Text.Json.JsonSerializer.Serialize(operations, ResoniteLink.LinkInterface.SerializationOptions);
                    Debug.Log(json);
                }

                var response = await Link.RunDataModelOperationBatch(messages);

                if (!response.Success)
                    throw new InvalidOperationException($"Data model batch operation failed: {response.ErrorInfo}");

                foreach (var subResponse in response.Responses)
                    if (!subResponse.Success)
                        throw new InvalidOperationException($"Operation failed for {subResponse.SourceMessageID}: {subResponse.ErrorInfo}");
            }).GetAwaiter().GetResult();
        }

        _assetConverter.ProcessConversions(Link);
    }

    void ProcessRemovals(List<DataModelOperation> messages)
    {
        List<Transform> transformsToRemove = null;

        foreach (var pair in _transformMap)
        {
            // I don't like that Unity does it this way, but this is how it checks if it's destroyed
            if (pair.Key != null)
                continue;

            if (transformsToRemove == null)
                transformsToRemove = new List<Transform>();

            // It's not actually null! It just pretends to be.
            transformsToRemove.Add(pair.Key);

            messages.Add(new RemoveSlot()
            {
                MessageID = GetUniqueMessageId($"RemoveSlot_{pair.Value.Name}"),
                SlotID = pair.Value.ID,
            });
        }

        if (transformsToRemove != null)
            foreach (var remove in transformsToRemove)
            {
                _existingSlots.Remove(remove);
                _transformMap.Remove(remove);
            }

        List<ResoniteComponent> componentsToRemove = null;

        // Do the components next
        foreach (var component in _existingComponents)
        {
            if (component.Key != null)
                continue;

            // Check if the transform itself is removed also
            // We need to do this through the dictionary, because we can't access transform on the component itself
            // when it has been removed.
            if (component.Value != null)
            {
                // The transform it exists on still exists, so we need to remove it explicitly
                // Otherwise it will be removed with the transform/slot, so we don't need to send message for it
                messages.Add(component.Key.GenerateRemoval(this));
            }

            // We still need to remove it
            if (componentsToRemove == null)
                componentsToRemove = new List<ResoniteComponent>();

            componentsToRemove.Add(component.Key);

            // Make sure all the ID's are cleaned up too
            component.Key.RemoveIDs(this);
        }

        if (componentsToRemove != null)
            foreach (var remove in componentsToRemove)
                _existingComponents.Remove(remove);
    }

    void UpdateComponentConversions(Transform root)
    {
        var components = new List<UnityEngine.Component>();

        root.GetComponents<UnityEngine.Component>(components);
        var converterMap = new Dictionary<UnityEngine.Component, ResoniteComponentConverter>();

        // First get all the converters
        foreach (var component in components)
            if (component is ResoniteComponentConverter converter)
            {
                // Check if the target still exists
                if (converter.Target == null)
                {
                    // This destroys the converter component alone (its GameObject survives), so
                    // its Resonite-side wrapper component(s) won't be cleaned up by Unity's own
                    // destroy cascade - Cleanup() needs to do that explicitly (see
                    // ResoniteComponentConverter.cs's ExplicitCleanupRequested field comment).
                    converter.ExplicitCleanupRequested = true;
                    UnityEngine.Object.DestroyImmediate(converter);
                }
                else
                    converterMap.Add(converter.Target, converter);
            }

        // Re-fetch the components, because some might've been destroyed in the previous step
        components.Clear();
        root.GetComponents<UnityEngine.Component>(components);

        // Filter out the converters or the converted components, those don't need to be converted!
        components.RemoveAll(c => c == null || c is ResoniteComponentConverter || c is ResoniteComponent);
        components.RemoveAll(c => !ShouldUpdateUnityComponentForActivePass(c));

        // Get converters for all the types we have
        var converters = new Dictionary<UnityEngine.Component, ConverterInfo>();

        foreach (var component in components)
        {
            var converter = ComponentConverterRepository.TryGetConverter(component.GetType());

            if (converter == null)
                continue;

            converters.Add(component, converter);
        }

        // Run supression for all converters if present. This will remove any components that should not be converted directly
        foreach (var converter in converters.Values)
            converter.SupressionHandler?.Invoke(root, components);

        // Update/instantiate converters for all the components that we should process
        foreach (var component in components)
        {
            // We might've just destroyed some of the components in previous iterations - e.g. converters
            // can add/remove more components, so skip those just in case
            if (component == null)
                continue;

            if (!converterMap.TryGetValue(component, out var converter))
            {
                // There's no existing converter for this. Check if one is supported. If not ignore it
                if (!converters.TryGetValue(component, out var converterInfo))
                    continue;

                // Create a new converter instance
                converter = (ResoniteComponentConverter)root.gameObject.AddComponent(converterInfo.Type);
                converter.Initialize(component);

                converterMap.Add(component, converter);

                // Check if there's defered actions
                if (_deferedActions.TryGetValue(component, out var list))
                {
                    foreach (var action in list)
                        action();

                    _deferedActions.Remove(component);
                }
            }

            // Update the conversion. This should handle both cases when it was freshly added
            // As well as when this is an existing one and we're updating components
            converter.UpdateConversion(this);
        }

        // Process children recursively
        for (int i = 0; i < root.childCount; i++)
        {
            var child = root.GetChild(i);
            UpdateComponentConversions(child);
        }
    }

    void ConvertHierarchy(Transform root, List<DataModelOperation> messages)
    {
        Convert(root, messages);
        ConvertComponents(root, messages);

        // Process children recursively
        for (int i = 0; i < root.childCount; i++)
        {
            var child = root.GetChild(i);
            ConvertHierarchy(child, messages);
        }
    }

    void Convert(Transform transform, List<DataModelOperation> messages)
    {
        AddUpdateSlotData message;

        // 2026-07-14: every entry point (the regular Convert path, RetryMissingAssetURLs, and
        // realtime sync's TransformUpdated) passes through here, so we must always guarantee
        // _importRootSlot exists right before handling a top-level Unity root. EnsureImportRootSlot
        // is idempotent, so it's harmless even if another caller has already invoked it.
        if (transform.parent == null)
            EnsureImportRootSlot(messages);

        var slot = GetLinkSlot(transform);

        if (_existingSlots.Add(transform))
        {
            message = new AddSlot();
            message.MessageID = GetUniqueMessageId($"AddSlot_{transform.name})");
        }
        else
        {
            message = new UpdateSlot();
            message.MessageID = GetUniqueMessageId($"UpdateSlot_{transform.name})");
        }

        GatherTransformData(transform, slot);

        message.Data = slot;

        messages.Add(message);
    }

    // Ensures the single "Unity Import" wrapper slot exists (creating it the first time this
    // SceneConverter instance runs a conversion, updating it on subsequent calls within the same
    // session), queues its Add/UpdateSlot message, and returns its ID so top-level Unity
    // transforms can target it as their parent instead of the world's literal "Root".
    // See the 2026-07-14 comment in Convert(IEnumerable<Transform>) for why this exists.
    string EnsureImportRootSlot(List<DataModelOperation> messages)
    {
        if (_importRootSlot == null)
        {
            // 2026-08-08 (per Tanossy's feedback: "shouldn't this have overwritten the old one?"):
            // previously, this code unconditionally issued a brand-new ID (AllocateId) and sent
            // AddSlot, so every reconnect (i.e. every time the session ID changed) piled up an
            // entirely separate "Unity Import" tree directly under World Root in parallel (the
            // Update-side branch was only designed to kick in on the 2nd+ call within the same
            // session).
            //
            // A true diff-based update (matching each existing child slot by ID and reusing it)
            // would require a much bigger redesign, since this SceneConverter instance doesn't
            // hold state across sessions by design. Instead we take a simpler and more reliable
            // approach here: on the first send of a new session, query once whether a slot with
            // the same name ("Unity Import") already exists directly under World Root, and if
            // found, delete it entirely before creating a fresh one. This isn't a true
            // "diff-based update" but rather "clean up, then rebuild" - but the end result is
            // that there is always exactly one tree, which achieves the "overwrite" effect the
            // user expects.
            // 2026-08-08: the lookup logic was moved to ImportRootSlotHelper (externalized into
            // Editor/ImportRootSlotHelper.cs).
            var staleRootId = ImportRootSlotHelper.TryFindExistingId(Link, ImportRootSlotName);

            if (staleRootId != null)
            {
                messages.Add(new RemoveSlot()
                {
                    MessageID = GetUniqueMessageId($"RemoveSlot_stale_{ImportRootSlotName}"),
                    SlotID = staleRootId,
                });
            }

            _importRootSlot = new ResoniteLink.Slot();

            _importRootSlot.ID = AllocateId();
            _importRootSlot.Parent = new Reference() { ID = AllocateId() };
            _importRootSlot.Position = new Field_float3() { ID = AllocateId() };
            _importRootSlot.Rotation = new Field_floatQ() { ID = AllocateId() };
            _importRootSlot.Scale = new Field_float3() { ID = AllocateId() };
            _importRootSlot.Name = new Field_string() { ID = AllocateId() };
            _importRootSlot.Tag = new Field_string() { ID = AllocateId() };
            _importRootSlot.IsActive = new Field_bool() { ID = AllocateId() };
        }

        _importRootSlot.Parent.TargetID = "Root";
        _importRootSlot.Position.Value = Vector3.zero.ToResoniteLink();
        _importRootSlot.Rotation.Value = Quaternion.identity.ToResoniteLink();
        _importRootSlot.Scale.Value = Vector3.one.ToResoniteLink();
        _importRootSlot.Name.Value = ImportRootSlotName;
        _importRootSlot.Tag.Value = null;
        _importRootSlot.IsActive.Value = true;

        AddUpdateSlotData message;

        if (!_importRootSlotAdded)
        {
            message = new AddSlot();
            message.MessageID = GetUniqueMessageId($"AddSlot_{ImportRootSlotName}");
            _importRootSlotAdded = true;
        }
        else
        {
            message = new UpdateSlot();
            message.MessageID = GetUniqueMessageId($"UpdateSlot_{ImportRootSlotName}");
        }

        message.Data = _importRootSlot;
        messages.Add(message);

        return _importRootSlot.ID;
    }

    ResoniteLink.Slot GetLinkSlot(Transform transform)
    {
        if (!_transformMap.TryGetValue(transform, out var slot))
        {
            slot = new ResoniteLink.Slot();

            slot.ID = AllocateId();

            slot.Parent = new Reference() { ID = AllocateId() };

            slot.Position = new Field_float3() { ID = AllocateId() };
            slot.Rotation = new Field_floatQ() { ID = AllocateId() };
            slot.Scale = new Field_float3() { ID = AllocateId() };
            slot.Name = new Field_string() { ID = AllocateId() };
            slot.Tag = new Field_string() { ID = AllocateId() };
            slot.IsActive = new Field_bool() { ID = AllocateId() };

            _transformMap.Add(transform, slot);
        }

        return slot;
    }

    void GatherTransformData(Transform transform, ResoniteLink.Slot data)
    {
        // 2026-07-14: top-level Unity roots now parent under the single "Unity Import" wrapper
        // slot (see EnsureImportRootSlot) instead of the world's literal "Root", so all of our
        // converted content lives under one well-known, easy-to-clean-up container.
        if (transform.parent == null)
            data.Parent.TargetID = _importRootSlot.ID;
        else
            data.Parent.TargetID = _transformMap[transform.parent].ID;

        data.Position.Value = transform.localPosition.ToResoniteLink();
        data.Rotation.Value = transform.localRotation.ToResoniteLink();
        data.Scale.Value = transform.localScale.ToResoniteLink();

        data.Name.Value = transform.name;

        if (transform.tag == "Untagged")
            data.Tag.Value = null;
        else
            data.Tag.Value = transform.tag;

        data.IsActive.Value = transform.gameObject.activeSelf;
    }

    void ConvertComponents(Transform transform, List<DataModelOperation> messages)
    {
        var components = transform.GetComponents<ResoniteComponent>();

        foreach (var c in components)
        {
            if (!ShouldSendResoniteComponentForActivePass(c))
                continue;

            var data = c.CollectData(this);

            if (_existingComponents.TryAdd(c, c.transform))
            {
                // We just added this component, so we need to generate add component message

                // We must assign the type when we're adding it
                // For updates we skip, since it's no longer necessary
                data.ComponentType = c.TypeName;

                var addComponent = new AddComponent()
                {
                    MessageID = GetUniqueMessageId($"AddComponent_{c.GetType().Name}"),
                    ContainerSlotId = GetTransformSlotId(c.transform),
                    Data = data,
                };

                messages.Add(addComponent);
            }
            else
            {
                var updateComponent = new UpdateComponent()
                {
                    MessageID = GetUniqueMessageId($"UpdateComponent_{c.GetType().Name}"),
                    Data = data
                };

                messages.Add(updateComponent);
            }
        }
    }

    bool ShouldUpdateUnityComponentForActivePass(UnityEngine.Component component)
    {
        switch (ActiveConversionPass)
        {
            case ResoniteSdkConversionPass.MeshesOnly:
                return component is UnityEngine.MeshRenderer ||
                    component is UnityEngine.SkinnedMeshRenderer ||
                    component is UnityEngine.MeshCollider ||
                    component is UnityEngine.ParticleSystem;

            case ResoniteSdkConversionPass.MaterialsOnly:
                return component is UnityEngine.MeshRenderer ||
                    component is UnityEngine.SkinnedMeshRenderer;

            case ResoniteSdkConversionPass.LightmapsOnly:
                return IsLightmappedMeshRenderer(component);

            default:
                return true;
        }
    }

    bool ShouldSendResoniteComponentForActivePass(ResoniteComponent component)
    {
        switch (ActiveConversionPass)
        {
            case ResoniteSdkConversionPass.MeshesOnly:
                return component is FrooxEngine.MeshRendererWrapper ||
                    component is FrooxEngine.SkinnedMeshRendererWrapper ||
                    component is FrooxEngine.MeshColliderWrapper ||
                    component is FrooxEngine.ConvexHullColliderWrapper ||
                    component.GetComponent<MeshConverter>() != null;

            case ResoniteSdkConversionPass.MaterialsOnly:
                return component is FrooxEngine.MeshRendererWrapper ||
                    component is FrooxEngine.SkinnedMeshRendererWrapper ||
                    component.GetComponent<ResoniteMaterialConverter>() != null ||
                    component.GetComponent<Texture2DConverter>() != null ||
                    component.GetComponent<CubemapConverter>() != null;

            case ResoniteSdkConversionPass.LightmapsOnly:
                return component is FrooxEngine.MeshRendererWrapper ||
                    component.GetComponent<BakedLightmapStandardConverter>() != null ||
                    IsGeneratedLightmapTextureConverter(component.GetComponent<Texture2DConverter>());

            default:
                return true;
        }
    }

    static bool IsLightmappedMeshRenderer(UnityEngine.Component component)
    {
        return component is UnityEngine.MeshRenderer renderer &&
            renderer.lightmapIndex >= 0 &&
            renderer.lightmapIndex < LightmapSettings.lightmaps.Length;
    }

    static bool IsGeneratedLightmapTextureConverter(Texture2DConverter converter)
    {
        return converter != null &&
            converter.Source != null &&
            converter.Source.name.StartsWith("LMTex_", StringComparison.OrdinalIgnoreCase);
    }

    void ObjectChangeEvents_changesPublished(ref ObjectChangeEventStream stream)
    {
        _assetConverter.BeginConversion();

        var processedTransforms = new HashSet<Transform>();
        var transformsWithChangedComponents = new HashSet<Transform>();
        var messages = new List<DataModelOperation>();

        void TransformUpdated(Transform transform)
        {
            if (!processedTransforms.Add(transform))
                return;

            Convert(transform, messages);
        }

        void ComponentUpdated(UnityEngine.Component component)
        {
            // We want to process the transforms as whole, since the combinations of components might require
            // filtering and other things, so we just collect all the transforms that had their components changed
            transformsWithChangedComponents.Add(component.transform);
        }

        void ObjectChanged(UnityEngine.Object o, bool forceComponentUpdate = false, bool recursive = false)
        {
            switch (o)
            {
                case Transform transform:
                    TransformUpdated(transform);

                    if (forceComponentUpdate)
                        transformsWithChangedComponents.Add(transform);

                    if (recursive)
                        for (int i = 0; i < transform.childCount; i++)
                            ObjectChanged(transform.GetChild(i), forceComponentUpdate, recursive);
                    break;

                case GameObject gameObject:
                    ObjectChanged(gameObject.transform, forceComponentUpdate, recursive);
                    break;

                case UnityEngine.Component component:
                    ComponentUpdated(component);
                    break;

                default:
                    Debug.LogWarning($"Unsupported object changed: {o}");
                    break;
            }
        }

        bool gameObjectsDestroyed = false;

        for (int i = 0; i < stream.length; i++)
        {
            switch (stream.GetEventType(i))
            {
                case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                    stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var changeObject);
                    ObjectChanged(EditorUtility.InstanceIDToObject(changeObject.instanceId));
                    break;

                case ObjectChangeKind.CreateGameObjectHierarchy:
                    stream.GetCreateGameObjectHierarchyEvent(i, out var createObject);
                    ObjectChanged(EditorUtility.InstanceIDToObject(createObject.instanceId), true, recursive: true);
                    break;

                case ObjectChangeKind.ChangeGameObjectStructure:
                    stream.GetChangeGameObjectStructureEvent(i, out var changeStructure);
                    ObjectChanged(EditorUtility.InstanceIDToObject(changeStructure.instanceId), true);
                    break;

                case ObjectChangeKind.ChangeGameObjectParent:
                    stream.GetChangeGameObjectParentEvent(i, out var changeParent);
                    ObjectChanged(EditorUtility.InstanceIDToObject(changeParent.instanceId));
                    break;

                case ObjectChangeKind.DestroyGameObjectHierarchy:
                    // TODO!!! Should we keep track of the ID's and only target ones that are actually destroyed?
                    // This requires a bunch of extra bookkeeping, and just running the removals is simpler here
                    // but it might not perform the best for large scenes, so we'll have to re-evaluate the approach.
                    gameObjectsDestroyed = true;
                    break;

                case ObjectChangeKind.ChangeAssetObjectProperties:
                    stream.GetChangeAssetObjectPropertiesEvent(i, out var changeAsset);
                    var changedAsset = EditorUtility.InstanceIDToObject(changeAsset.instanceId);

                    // Force an asset update for any assets that have been converted
                    // Ignore everything else - it's not an asset we transferred over yet, so we don't need to bother with it
                    // Once it gets referenced by something, e.g. a component that will ensure conversion happens
                    switch (changedAsset)
                    {
                        case UnityEngine.Mesh mesh:
                            if (_assetConverter.HasMesh(mesh))
                                _assetConverter.GetMesh(mesh);
                            break;

                        case UnityEngine.Texture2D texture2D:
                            if (_assetConverter.HasTexture2D(texture2D))
                                _assetConverter.GetTexture2D(texture2D);
                            break;

                        case UnityEngine.Cubemap cubemap:
                            if (_assetConverter.HasCubemap(cubemap))
                                _assetConverter.GetCubemap(cubemap);
                            break;

                        case UnityEngine.AudioClip audioClip:
                            if (_assetConverter.HasAudioClip(audioClip))
                                _assetConverter.GetAudioClip(audioClip);
                            break;

                        case UnityEngine.Material material:
                            if (_assetConverter.HasMaterial(material))
                                _assetConverter.GetMaterial(material);
                            break;
                    }
                    break;

                default:
                    Debug.Log($"Change: {stream.GetEventType(i)}");
                    break;
            }
        }

        // Update the conversions first
        foreach (var changedComponents in transformsWithChangedComponents)
            UpdateComponentConversions(changedComponents);

        // Convert the actual components
        foreach (var changedComponents in transformsWithChangedComponents)
            ConvertComponents(changedComponents, messages);

        if (gameObjectsDestroyed)
            ProcessRemovals(messages);

        // Convert any updated asset providers
        // We don't need to run the component conversion on these - this should be only the bindings
        if (_assetConverter.HasPendingChanges)
            foreach (var root in _assetConverter.UpdatedAssetProviderRoots)
                ConvertHierarchy(root, messages);

        // If nothing of relevance was changed, just skip
        if (messages.Count == 0 && !_assetConverter.HasPendingChanges)
            return;

        SendOperationBatch(messages);
    }

    public Task<MethodResult> CallMethod(CallSyncMethod request) => Link.CallMethod(request);
    public Task<MethodResult> CallMethod(CallStaticSyncMethod request) => Link.CallStaticMethod(request);

    public void RunOnConverted(UnityEngine.Component component, Action action)
    {
        // Check if it's already converted and run the action right away
        if (component.GetComponents<ResoniteComponentConverter>().Any(c => c.Target == component))
        {
            action();
            return;
        }

        // This behavior hasn't been converted yet, so we need to defer this action
        if (!_deferedActions.TryGetValue(component, out var list))
        {
            list = new List<Action>();
            _deferedActions.Add(component, list);
        }

        list.Add(action);
    }

    // ------------------------------------------------------------------
    // Light Tuning Panel input hand-off (2026-08-26)
    // ------------------------------------------------------------------
    //
    // See the call site in Convert(IEnumerable<Transform>) for the full "why" (replaces the old
    // LightTuningPanelBuilder.cs C#-side UIX/ProtoFlux construction, per Tanossy's direction to
    // match the already-proven 2026-08-24 hand-built raw-JSON version instead of continuing to
    // debug this SDK's typed component wrappers layer by layer). This half is deliberately tiny:
    // gather each already-converted light's baseline data, write it to one JSON file, and launch
    // scripts/build_light_tuning_panel.py (in the eldorado repo) to do the actual ResoniteLink
    // work. Fire-and-forget - does not block this Editor's main thread waiting for the script.

    // 2026-08-26: these use auto-PROPERTIES, not plain public fields, deliberately -
    // System.Text.Json.JsonSerializer.Serialize(obj) with DEFAULT options (no explicit
    // JsonSerializerOptions passed) only serializes public PROPERTIES, not public fields
    // (IncludeFields defaults to false). The existing SendOperationBatch's debug-log call a few
    // methods above this one only works with plain fields because it explicitly passes
    // ResoniteLink.LinkInterface.SerializationOptions (which must set IncludeFields = true for
    // that whole message-class hierarchy, all plain-field-based, to serialize at all) - rather
    // than depend on that specific options object's exact configuration for this unrelated DTO,
    // these are just properties so a bare Serialize(payload) call works correctly on its own.
    [Serializable]
    class LightTuningPanelInputLight
    {
        public string SlotId { get; set; }
        public string Name { get; set; }
        public float BaselineIntensity { get; set; }
        public float[] BaselineColor { get; set; } // [r, g, b, a]

        // World-space position of the *Unity* Transform (transform.position, not
        // transform.localPosition) at send time - used only for placing the panel "near" the
        // lights, not sent to Resonite as-is. Since every Unity scene root ends up parented
        // under the "Unity Import" wrapper slot, which itself always sits at Resonite-world
        // origin with an identity rotation/scale (see EnsureImportRootSlot), a light's Unity
        // world position is a direct, unscaled stand-in for its eventual Resonite-world
        // position - good enough for "roughly where the room is", which is all placement needs.
        public float[] Position { get; set; } // [x, y, z]
    }

    [Serializable]
    class LightTuningPanelInputPayload
    {
        public int Port { get; set; }
        public List<LightTuningPanelInputLight> Lights { get; set; } = new List<LightTuningPanelInputLight>();

        // 2026-08-30 (Lumos-derived, see LightmapTuningPayload.cs): field IDs the panel script
        // wires into one ValueMultiDriver each - AlbedoColor of every white-baseline lightmapped
        // material ("LightTuning/LightmapTint") and PreferredFormat of every lightmap texture
        // ("LightTuning/LightmapLossless"). LightmapTintSkipped lists materials left undriven
        // because their authored color isn't white.
        public List<string> LightmapTintTargets { get; set; } = new List<string>();
        public List<string> LightmapFormatTargets { get; set; } = new List<string>();
        public List<string> LightmapTintSkipped { get; set; } = new List<string>();
        // 2026-08-31: initial value for the in-world LightmapTint driver = the AlbedoGain the
        // converter already applied (see LightmapTuningPayload.TintDefault). [r, g, b]
        public float[] LightmapTintDefault { get; set; } = new[] { 1f, 1f, 1f };
    }

    // Absolute path to the eldorado monorepo checkout that owns build_light_tuning_panel.py and
    // this feature's wiki writeup - not part of this Unity project, so it can't be found via
    // Application.dataPath. Matches the fixed path already used throughout that repo's own docs/
    // scripts for this machine (see e.g. this file's own historical comments referencing
    // C:/urd/wiki/... and C:/Repositories/eldorado/scripts/...).
    const string EldoradoRepoRoot = @"c:/Repositories/eldorado";

    void WriteLightTuningPanelInputAndLaunchBuilder()
    {
        try
        {
            var lights = UnityEngine.Object.FindObjectsOfType<LightConverter>()
                .Where(c => c != null && c.Target != null && c.Binding != null && c.Binding.Data != null)
                .ToList();

            var payload = new LightTuningPanelInputPayload { Port = _window.Port };

            // 2026-08-30: lightmap tint/lossless targets (Lumos-derived) - gathered before the
            // "no lights" early-out below, since a scene can have baked lightmaps and no live
            // Light components and still want the lightmap controls.
            LightmapTuningPayload.Fill(this, payload.LightmapTintTargets, payload.LightmapFormatTargets, payload.LightmapTintSkipped);
            payload.LightmapTintDefault = LightmapTuningPayload.TintDefault();

            if (lights.Count == 0 && payload.LightmapTintTargets.Count == 0 && payload.LightmapFormatTargets.Count == 0)
            {
                Debug.Log("[LightTuningPanel] No lights and no lightmapped materials/textures found in this scene - skipping build_light_tuning_panel.py.");
                return;
            }

            foreach (var lc in lights)
            {
                // Baseline values are read off the already-populated FrooxEngine.Light data
                // (LightHelper.SetFrom already ran during UpdateComponentConversions, earlier in
                // this same Convert() call, so these already include LightTuning.ApplyIntensity/
                // ApplyColor - i.e. they match exactly what was just sent to Resonite for this
                // light, not the raw pre-tuning Unity values).
                var data = lc.Binding.Data;
                var c = data.Color.color;

                var pos = lc.transform.position;

                payload.Lights.Add(new LightTuningPanelInputLight
                {
                    SlotId = GetTransformSlotId(lc.transform),
                    Name = lc.Target.name,
                    BaselineIntensity = data.Intensity,
                    BaselineColor = new[] { c.r, c.g, c.b, c.a },
                    Position = new[] { pos.x, pos.y, pos.z },
                });
            }

            var projectDir = System.IO.Directory.GetParent(Application.dataPath).FullName;
            var tempDir = System.IO.Path.Combine(projectDir, "Temp");
            System.IO.Directory.CreateDirectory(tempDir);

            var inputPath = System.IO.Path.Combine(tempDir, "light_tuning_panel_input.json");
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            System.IO.File.WriteAllText(inputPath, json);

            var logPath = System.IO.Path.Combine(tempDir, "light_tuning_panel_result.txt");
            LaunchLightTuningPanelBuilder(inputPath, logPath);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[LightTuningPanel] Failed to write input JSON / launch build script:\n{ex}");
        }
    }

    // 2026-08-26 (per Tanossy's report of a texture-upload disconnect on two SEPARATE sessions/
    // ports, and the coordinator's follow-up asking to rule out - rather than assume away -
    // whether this fire-and-forget background process could still be contributing): reading
    // SceneConverter.Convert()'s own code confirms this method can only ever run AFTER
    // SendOperationBatch(messages) has already returned successfully (it's called after that line,
    // inside the same try block - if SendOperationBatch throws, as it does when asset conversion
    // fails, the catch block below skips this method entirely for that Convert() call). So this
    // process cannot be running *during* the very asset upload that fails in the same Send Current
    // Scene click. It CAN still be running in the background for the ~30-90s it normally takes
    // (0.4s wait x ~18 round trips x 7 lights, plus network latency) *after* Convert() has already
    // returned and the Editor is responsive again - if the user triggers another Resonite-related
    // action (including a second Send Current Scene) inside that window, a second WebSocket client
    // from an *earlier* successful send would legitimately still be talking to the *same* live
    // session while a *new* send's asset upload is in flight. This lock file doesn't prove that
    // scenario causes anything (the leading, evidence-backed explanation for the reported crash is
    // a pre-existing, already-documented ResoniteLink.dll race condition - see AssetConversionManager
    // .ProcessConversions' 2026-08-08 comment - colliding with an unusually large asset: skybox_
    // stars_render.png is ~68MB, by far the largest texture in this scene, so it has by far the
    // longest transfer window for that pre-existing bug to manifest in, regardless of this file).
    // It simply closes off an entire class of "could this be an aggravating factor" doubt cheaply:
    // never let two build_light_tuning_panel.py runs be in flight against the same machine at once.
    static string LightTuningPanelLockPath(string tempDir) =>
        System.IO.Path.Combine(tempDir, "light_tuning_panel.lock");

    static void LaunchLightTuningPanelBuilder(string inputPath, string logPath)
    {
        var tempDir = System.IO.Path.GetDirectoryName(inputPath);
        var lockPath = LightTuningPanelLockPath(tempDir);

        if (System.IO.File.Exists(lockPath) &&
            int.TryParse(System.IO.File.ReadAllText(lockPath).Trim(), out var previousPid) &&
            IsProcessRunning(previousPid))
        {
            AppendLightTuningPanelLog(logPath,
                $"Skipped launch: a previous build_light_tuning_panel.py run (pid={previousPid}) is " +
                "still in progress. Not starting a second concurrent WebSocket client against the " +
                "same session - wait for it to finish (see this file, Temp/light_tuning_panel_result.txt) " +
                "before sending again.");
            return;
        }

        var scriptPath = System.IO.Path.Combine(EldoradoRepoRoot, "scripts", "build_light_tuning_panel.py");

        // The panel builder is an optional companion script that lives outside this Unity project
        // (see EldoradoRepoRoot). On any checkout that doesn't have it, Process.Start would throw a
        // Win32Exception and surface as a red console error on every single send, so skip quietly
        // instead: everything the panel drives (light intensity, LightmapTint, lossless textures)
        // has already been baked into the values that were just sent.
        if (!System.IO.File.Exists(scriptPath))
        {
            AppendLightTuningPanelLog(logPath,
                $"Skipped launch: optional in-world Light Tuning Panel builder not found at {scriptPath}. " +
                "The scene itself was sent normally; only the in-world tuning panel is skipped.");
            return;
        }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "uv",
            Arguments = $"run python \"{scriptPath}\" --input \"{inputPath}\"",
            WorkingDirectory = EldoradoRepoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // Known Windows gotcha (already documented elsewhere in this project's own notes):
        // Python's stdout defaults to the console's OEM code page (cp932 on this machine),
        // which throws UnicodeEncodeError the moment anything non-ASCII gets printed. The
        // script's own runtime log lines are plain ASCII today, but this is cheap, harmless
        // insurance against that ever silently breaking a future edit to the script.
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

        var process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };

        process.OutputDataReceived += (s, e) => { if (e.Data != null) AppendLightTuningPanelLog(logPath, "[out] " + e.Data); };
        process.ErrorDataReceived += (s, e) => { if (e.Data != null) AppendLightTuningPanelLog(logPath, "[err] " + e.Data); };
        process.Exited += (s, e) =>
        {
            AppendLightTuningPanelLog(logPath, $"[build_light_tuning_panel.py exited with code {process.ExitCode}]");

            try
            {
                // Only clear the lock if it's still ours - a stale-PID cleanup elsewhere or a
                // brand new run could in principle have already replaced it.
                if (System.IO.File.Exists(lockPath) &&
                    int.TryParse(System.IO.File.ReadAllText(lockPath).Trim(), out var lockedPid) &&
                    lockedPid == process.Id)
                    System.IO.File.Delete(lockPath);
            }
            catch
            {
                // Best-effort cleanup only - a leftover lock file just means the *next* launch
                // attempt does one extra (harmless) IsProcessRunning() check that returns false.
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        // Deliberately no WaitForExit() - fire-and-forget, matching Tanossy's explicit "don't
        // block the Editor waiting for the process" instruction. Progress/errors land in
        // Temp/light_tuning_panel_result.txt (via the handlers above) rather than in Unity's own
        // Console, since this process is fully detached from Unity's own stdout.

        System.IO.File.WriteAllText(lockPath, process.Id.ToString());

        AppendLightTuningPanelLog(logPath,
            $"Launched build_light_tuning_panel.py (input={inputPath}, pid={process.Id}).");
    }

    static bool IsProcessRunning(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            // No process with this id exists (already exited and the id was reused or is just gone).
            return false;
        }
    }

    static void AppendLightTuningPanelLog(string logPath, string line)
    {
        try
        {
            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss}] {line}\n");
        }
        catch
        {
            // Best-effort logging only - never let a logging failure take down the (already
            // fire-and-forget, already-launched) build process or anything else in this class.
        }
    }
}
