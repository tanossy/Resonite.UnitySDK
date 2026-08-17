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
                    UnityEngine.Object.DestroyImmediate(converter);
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
}
