using BepuPhysics.Collidables;
using FrooxEngine;
using ResoniteLink;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public struct AssetMap<A> : IEquatable<AssetMap<A>>
    where A : UnityEngine.Object
{
    public readonly A Asset;
    public readonly AssetMessagePostProcessor PostProcessor;

    // 2026-07-14 (Tanossy指摘): 元々はAsset(Unityオブジェクト参照)そのもので同一性を判定していた。
    // これだと ForceRefreshGeneratedLightmaps 等でファイルを削除→同じパスに再生成した場合、
    // 中身は同じ論理アセットでもUnity側の参照は別物になるため辞書照合が必ずミスし、
    // 既存のConverter(=既存のResonite側ID)を見つけられず毎回新規Slot/Component/IDを
    // 追加してしまう(=送信の度に重複が増える)。
    //
    // 2026-07-27 バグ修正（実機で発覚・Tanossy指摘）: アセットパスだけをキーにしていたのが誤りだった。
    // FBX等からインポートされた「同一ファイル内の複数サブアセット」(例: 1つの.fbxに入っている
    // Cube/curtain low poly/Cylinder.003/Plane等、別々のMeshオブジェクト)は全て同じ
    // AssetDatabase.GetAssetPath()を返す(コンテナファイルのパス)。パスだけで同一性判定すると、
    // 中身が全く違う複数のメッシュが「同じアセット」と誤認され、後から変換されたサブアセットが
    // 既存のConverterのSourceを上書きしてしまい、全オブジェクトが同じ1つのメッシュを指すという
    // 実害(形・テクスチャが完全に別物になる)が実機で確認された。GUID+ローカルファイルID
    // (Unityがファイル内の各サブアセットに割り振る安定IDのペア)をキーにすることで、
    // 「同じファイルへの再生成」と「同じファイル内の別サブアセット」を正しく区別する。
    // プロシージャル生成物(GUID/パスを持たない)は従来通り参照ベースにフォールバックする。
    readonly string _path;

    public AssetMap(A asset, AssetMessagePostProcessor postProcessor)
    {
        this.Asset = asset;
        PostProcessor = postProcessor;

        if (asset != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out var guid, out long localId)
            && !string.IsNullOrEmpty(guid))
        {
            _path = $"{guid}:{localId}";
        }
        else
        {
            _path = null;
        }
    }

    public bool Equals(AssetMap<A> other)
    {
        if (PostProcessor != other.PostProcessor)
            return false;

        if (_path != null && other._path != null)
            return _path == other._path;

        // At least one side has no stable path (procedural asset, or one/both sides null) -
        // only a path-vs-no-path pairing can never match here (intentional: a procedural asset
        // must never accidentally alias a persisted one), so fall back to reference identity.
        return _path == null && other._path == null && Asset == other.Asset;
    }

    public override int GetHashCode() =>
        _path != null
            ? HashCode.Combine(_path, PostProcessor)
            : HashCode.Combine(Asset, PostProcessor);
}

public class AssetConversionManager
{
    public const string ASSETS_ROOT_NAME = "__UnityAssets";

    public SceneConverter Converter { get; private set; }
    public Transform AssetsRoot { get; private set; }

    public bool HasPendingChanges => _scheduledConversions.Count > 0 || _updatedAssetProviderRoots.Count > 0;

    Dictionary<AssetMap<UnityEngine.Mesh>, MeshConverter> _meshes = new Dictionary<AssetMap<UnityEngine.Mesh>, MeshConverter>();
    Dictionary<AssetMap<UnityEngine.Texture2D>, Texture2DConverter> _textures = new Dictionary<AssetMap<UnityEngine.Texture2D>, Texture2DConverter>();
    Dictionary<AssetMap<UnityEngine.Cubemap>, CubemapConverter> _cubemaps = new Dictionary<AssetMap<UnityEngine.Cubemap>, CubemapConverter>();
    Dictionary<AssetMap<UnityEngine.AudioClip>, AudioClipConverter> _audioClips = new Dictionary<AssetMap<UnityEngine.AudioClip>, AudioClipConverter>();

    Dictionary<UnityEngine.Material, ResoniteMaterialConverter> _materials = new Dictionary<UnityEngine.Material, ResoniteMaterialConverter>();
    Dictionary<UnityEngine.Material, FrooxEngine.IAssetProvider<FrooxEngine.Material>> _cachedMaterials = new Dictionary<UnityEngine.Material, IAssetProvider<FrooxEngine.Material>>();

    HashSet<AssetConverter> _checkedConverters = new HashSet<AssetConverter>();
    Queue<AssetConverter> _scheduledConversions = new Queue<AssetConverter>();

    HashSet<Transform> _updatedAssetProviderRoots = new HashSet<Transform>();

    public IEnumerable<Transform> UpdatedAssetProviderRoots => _updatedAssetProviderRoots;

    public AssetConversionManager(SceneConverter converter)
    {
        Converter = converter;

        // Check if there's already assets root in the world
        var roots = SceneManager.GetActiveScene().GetRootGameObjects();

        AssetsRoot = roots.FirstOrDefault(r => r.name == ASSETS_ROOT_NAME)?.transform;

        if(AssetsRoot != null)
        {
            // Scan all the existing converters
            ScanConverters<StaticMesh, StaticMeshWrapper, UnityEngine.Mesh, FrooxEngine.Mesh, MeshConverter>(_meshes);
            ScanConverters<StaticTexture2D, StaticTexture2DWrapper, UnityEngine.Texture2D, FrooxEngine.Texture2D, Texture2DConverter>(_textures);
            ScanConverters<StaticCubemap, StaticCubemapWrapper, UnityEngine.Cubemap, FrooxEngine.Cubemap, CubemapConverter>(_cubemaps);
            ScanConverters<StaticAudioClip, StaticAudioClipWrapper, UnityEngine.AudioClip, FrooxEngine.AudioClip, AudioClipConverter>(_audioClips);

            // Materials are special!
            ScanMaterials();
        }
        else
            AssetsRoot = (new GameObject(ASSETS_ROOT_NAME)).transform; // Create new root
    }

    void ScanConverters<TProvider, TWrapper, TUnity, TResonite, TConverter>(Dictionary<AssetMap<TUnity>, TConverter> map)
        where TProvider : FrooxEngine.Component, IAssetProvider<TResonite>, new()
        where TWrapper : ResoniteComponent<TProvider>
        where TResonite : FrooxEngine.IAsset
        where TConverter : AssetConverter<TWrapper, TProvider, TUnity, TResonite>
        where TUnity : UnityEngine.Object
    {
        var converters = AssetsRoot.GetComponentsInChildren<TConverter>();

        foreach (var converter in converters)
        {
            if (converter.Source == null || converter.Provider == null)
            {
                // TODO!!! Cleanup?
                continue;
            }

            var key = new AssetMap<TUnity>(converter.Source, converter.PostProcessor);

            if (map.TryGetValue(key, out var existing))
            {
                if (existing.HasMissingProviderURL() && !converter.HasMissingProviderURL())
                    map[key] = converter;

                Debug.LogWarning($"[ResoniteSDK] Duplicate asset converter found for {converter.Source.name}. " +
                    "Keeping one converter and ignoring the duplicate during scan.");
                continue;
            }

            map.Add(key, converter);
        }
    }

    void ScanMaterials()
    {
        var converters = AssetsRoot.GetComponentsInChildren<ResoniteMaterialConverter>();

        foreach (var converter in converters)
        {
            if (converter.Source == null)
            {
                // TODO!!! Cleanup?
                continue;
            }

            _materials.Add(converter.Source, converter);
        }
    }

    public void BeginConversion()
    {
        // Clear all the cached materials from previous conversion. Unlike other asset types, materials can
        // change between conversions - e.g. their parameters are updated, so we want to re-run the conversion
        // every time.
        _cachedMaterials.Clear();

        // Since we're running a new conversion batch, we need to re-check all the converters again, because the assets
        // might have changed.
        _checkedConverters.Clear();
    }

    public int ScheduleMissingAssetURLRetries()
    {
        if (AssetsRoot == null)
            return 0;

        int scheduled = 0;

        foreach (var converter in AssetsRoot.GetComponentsInChildren<AssetConverter>(true))
        {
            if (converter == null || !converter.CanRetryMissingProviderURL())
                continue;

            if (_scheduledConversions.Contains(converter))
                continue;

            _scheduledConversions.Enqueue(converter);
            _updatedAssetProviderRoots.Add(converter.transform);
            scheduled++;
        }

        return scheduled;
    }

    public int RetryMissingAssetURLs(LinkInterface link)
    {
        var scheduled = ScheduleMissingAssetURLRetries();

        if (scheduled > 0)
            ProcessConversions(link);

        return scheduled;
    }

    public bool HasMesh(UnityEngine.Mesh mesh, AssetMessagePostProcessor postProcessor = null) =>
        _meshes.ContainsKey(new AssetMap<UnityEngine.Mesh>(mesh, postProcessor));

    public bool HasTexture2D(UnityEngine.Texture2D texture2D, AssetMessagePostProcessor postProcessor = null) =>
        _textures.ContainsKey(new AssetMap<UnityEngine.Texture2D>(texture2D, postProcessor));
    public bool HasCubemap(UnityEngine.Cubemap cubemap, AssetMessagePostProcessor postProcessor = null) =>
        _cubemaps.ContainsKey(new AssetMap<UnityEngine.Cubemap>(cubemap, postProcessor));
    public bool HasAudioClip(UnityEngine.AudioClip audioClip, AssetMessagePostProcessor postProcessor = null) => 
        _audioClips.ContainsKey(new AssetMap<UnityEngine.AudioClip>(audioClip, postProcessor));
    public bool HasMaterial(UnityEngine.Material material, AssetMessagePostProcessor postProcessor = null) => _materials.ContainsKey(material);

    public IAssetProvider<FrooxEngine.Mesh> GetMesh(UnityEngine.Mesh mesh, AssetMessagePostProcessor postProcessor = null) =>
        GetAsset<StaticMesh, StaticMeshWrapper, UnityEngine.Mesh, FrooxEngine.Mesh, MeshConverter>(
            mesh, postProcessor, _meshes);

    public IAssetProvider<FrooxEngine.Texture2D> GetTexture2D(UnityEngine.Texture2D texture, AssetMessagePostProcessor postProcessor = null) =>
        GetAsset<StaticTexture2D, StaticTexture2DWrapper, UnityEngine.Texture2D, FrooxEngine.Texture2D, Texture2DConverter>(
            texture, postProcessor, _textures);

    public IAssetProvider<FrooxEngine.Cubemap> GetCubemap(UnityEngine.Cubemap cubemap, AssetMessagePostProcessor postProcessor = null) =>
        GetAsset<StaticCubemap, StaticCubemapWrapper, UnityEngine.Cubemap, FrooxEngine.Cubemap, CubemapConverter>(
            cubemap, postProcessor, _cubemaps);

    public IAssetProvider<FrooxEngine.AudioClip> GetAudioClip(UnityEngine.AudioClip audioClip, AssetMessagePostProcessor postProcessor = null) =>
        GetAsset<StaticAudioClip, StaticAudioClipWrapper, UnityEngine.AudioClip, FrooxEngine.AudioClip, AudioClipConverter>(
            audioClip, postProcessor, _audioClips);

    TProvider GetAsset<TProvider, TWrapper, TUnity, TResonite, TConverter>(TUnity unity, AssetMessagePostProcessor postProcessor,
        Dictionary<AssetMap<TUnity>, TConverter> converters)
        where TProvider : FrooxEngine.Component, IAssetProvider<TResonite>, new()
        where TWrapper : ResoniteComponent<TProvider>
        where TResonite : FrooxEngine.IAsset
        where TConverter : AssetConverter<TWrapper, TProvider, TUnity, TResonite>
        where TUnity : UnityEngine.Object
    {
        if (unity == null)
            throw new ArgumentNullException(nameof(unity));

        bool needsToConvert = false;

        var identity = new AssetMap<TUnity>(unity, postProcessor);

        if (!converters.TryGetValue(identity, out var converter))
        {
            // There's no active converter for this, so create one
            var go = new GameObject();
            go.transform.parent = AssetsRoot;

            converter = go.AddComponent<TConverter>();
            converter.Initialize(unity, postProcessor);

            // Since it's brand new it needs to be converted for the first time
            needsToConvert = true;

            converters.Add(identity, converter);
        }
        else
        {
            // 2026-07-14: identity matched by stable asset path even though the live Unity object
            // reference may differ (e.g. the asset was deleted and regenerated at the same path -
            // see AssetMap's doc comment). Re-point the converter at the current live object before
            // checking for changes, both so HasAssetChanged() isn't evaluating a stale/destroyed
            // reference, and so the resulting update targets the ALREADY-SENT Resonite-side
            // component (UpdateComponent) instead of us spawning a duplicate.
            if (!ReferenceEquals(converter.Source, unity))
                converter.Source = unity;

            if (_checkedConverters.Add(converter) && converter.HasAssetChanged())
            {
                // We haven't checked this conversion yet for updates and the asset has changed
                // so we need to run the conversion again to update the data
                needsToConvert = true;
            }
        }

        if (needsToConvert)
        {
            _scheduledConversions.Enqueue(converter);

            _updatedAssetProviderRoots.Add(converter.Provider.transform);
        }

        return converter.Provider.Data;
    }

    public IAssetProvider<FrooxEngine.Material> GetMaterial(UnityEngine.Material material)
    {
        // Check if there's already conversion and it's bee updated
        if (_cachedMaterials.TryGetValue(material, out var provider))
            return provider;

        // Check if there's an active converter
        if (!_materials.TryGetValue(material, out var converter))
        {
            // There's no converter! Try to find one
            var converterType = MaterialConverterRepository.TryGetConverter(material);

            if(converterType == null)
            {
                Debug.LogWarning($"Unable to convert material {material}. Shader: {material.shader?.name}");

                // Set it to null. We still want to cache null converted material so we're not doing
                // this whole search & evaluation every single time this material is requested
                converter = null;
            }
            else
            {
                // Create the conversion
                var root = new GameObject($"Material - {material.name}");
                root.transform.parent = AssetsRoot;

                // Attach the converter itself
                converter = (ResoniteMaterialConverter)root.AddComponent(converterType);
                converter.Source = material;

                // We do want to store the converter across conversions - they will update existing material
                // in place in most cases, so we don't want to keep making new ones all the time
                _materials.Add(material, converter);
            }
        }

        // Update the conversion and get the latest material provider
        // This is important, because converter can change the actual material instance depending on the
        // properties provided. The converter can all be null, hence the null check
        provider = converter?.UpdateConversion(material, Converter);

        if (provider != null)
            _updatedAssetProviderRoots.Add(converter.transform);

        // Cache it for this run - the same material only needs to be converted/updated once per run
        _cachedMaterials.Add(material, provider);

        return provider;
    }

    public void ProcessConversions(LinkInterface link)
    {
        // 2026-08-08 (Tanossy指摘): EditorUtility.DisplayProgressBar/ClearProgressBarをこのループから
        // 完全に撤去した。このダイアログはUnity Editorのメインスレッド上でモーダル的なGUIイベント
        // ポンプを伴うため、ちょうどこのループが呼ぶjob.Convert()内部の非同期WebSocket送信
        // (Task.Run(...).Wait()でメインスレッドをブロックしつつ、別スレッドでSendAsync/ReceiverHandlerが
        // 動く構造)と時間的に重なる。ResoniteLink.dll側の未同期レースコンディション
        // (SendMessage送信中にReceiverHandlerが"no pending response with this ID"で誤爆し
        // _client.Dispose()する既知バグ、2026-07-30ソース読解で確定済み)の再現条件を、ダイアログの
        // 描画/イベント処理が意図せず広げている可能性を切り分けるため、SDK本来の送受信ロジックとは
        // 無関係なこのUI要素を除去し、干渉源から切り離す。
        while (_scheduledConversions.Count > 0)
        {
            if (link == null || !link.IsConnected)
            {
                throw new InvalidOperationException("Asset conversion stopped because ResoniteLink is no longer connected. Reconnect and run Send Current Scene again; conversion state will be rebuilt.");
            }

            var job = _scheduledConversions.Dequeue();

            try
            {
                job.Convert(Converter, link);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Asset conversion stopped while converting {job.AssetClass} {job.AssetName}. ResoniteLink likely disconnected during asset upload. " +
                    "Reconnect and run Send Current Scene again; conversion state will be rebuilt.",
                    ex);
            }
        }

        // Once conversions are processed, clear this. This is only relevant before the conversions take place
        _updatedAssetProviderRoots.Clear();
    }
}
