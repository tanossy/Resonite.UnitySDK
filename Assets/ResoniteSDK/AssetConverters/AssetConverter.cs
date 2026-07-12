using FrooxEngine;
using ResoniteLink;
using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

public abstract class AssetConverter : MonoBehaviour
{
    public AssetMessagePostProcessor PostProcessor;

    public ulong LastTimestamp;

    public void Convert(IConversionContext context, LinkInterface link)
    {
        var data = GenerateConversion();
        var name = AssetName;
        var timestamp = GetAssetTimestamp();

        // Run any postprocessing
        PostProcessor?.Process(data);

        Task.Run(async () =>
        {
            try
            {
                var result = await SendConversion(data, link);

                if (!result.Success)
                    throw new InvalidOperationException($"Failed to convert {AssetClass} {name}: {result.ErrorInfo}");

                // Assign the URL of the converted asset
                var updateData = UpdateProvider(result.AssetURL, context);

                // Send the update!
                // We could just do it all at once, but having it pop piece by piece is more interactive and looks cooler :3
                var update = new UpdateComponent()
                {
                    MessageID = context.GetUniqueMessageId($"Update{AssetClass}Provider_{name}"),
                    Data = updateData,
                };

                var updateResult = await link.UpdateComponent(update);

                if (!updateResult.Success)
                    throw new InvalidOperationException($"Failed to update asset provider with URL: {updateResult.ErrorInfo}");
            }
            catch
            {
                ClearProviderURL();
                throw;
            }
        }).Wait();

        // Store the timestamp only after both the upload and provider URL update succeeded. Failed
        // conversions must not look cached/current on the next send.
        LastTimestamp = timestamp;
    }

    public virtual bool HasAssetChanged() => GetAssetTimestamp() != LastTimestamp;

    public virtual bool HasMissingProviderURL() => false;

    public virtual bool CanRetryMissingProviderURL() => false;

    protected virtual void ClearProviderURL() { }

    protected abstract ulong GetAssetTimestamp();

    public abstract string AssetClass { get; }
    public abstract string AssetName { get; }
    protected abstract ResoniteLink.Message GenerateConversion();
    protected abstract Task<AssetData> SendConversion(ResoniteLink.Message message, LinkInterface link);
    protected abstract ResoniteLink.Component UpdateProvider(Uri assetUrl, IConversionContext context);
}

public abstract class AssetConverter<TWrapper, TProvider, TUnity, TResonite> : AssetConverter
    where TWrapper : ResoniteComponent<TProvider>
    where TProvider : FrooxEngine.Component, IAssetProvider<TResonite>, new()
    where TResonite : FrooxEngine.IAsset
    where TUnity : UnityEngine.Object
{
    public TUnity Source;
    public TWrapper Provider;

    public void Initialize(TUnity source, AssetMessagePostProcessor postProcessor)
    {
        if (Source != null || Provider != null)
            throw new InvalidOperationException("This converter has already been initialized");

        if (source == null)
            throw new ArgumentNullException(nameof(source));

        this.Source = source;
        this.PostProcessor = postProcessor;

        gameObject.name = $"{typeof(TResonite).Name} - {AssetName}";

        Provider = gameObject.AddComponent<TWrapper>();

        Provider.Data.Enabled = true;
        Provider.Data.persistent = true;
    }

    public override bool HasAssetChanged()
    {
        // If the URL is missing, it needs to be converted again
        if (Provider.Data is IStaticAssetProvider staticProvider && staticProvider.URL == null)
            return true;

        if (PostProcessor != null && PostProcessor.HasChanged())
            return true;

        return base.HasAssetChanged();
    }

    public override bool HasMissingProviderURL() =>
        Provider?.Data is IStaticAssetProvider staticProvider && staticProvider.URL == null;

    public override bool CanRetryMissingProviderURL() =>
        Source != null && Provider != null && HasMissingProviderURL();

    protected override void ClearProviderURL()
    {
        if (Provider?.Data is IStaticAssetProvider staticProvider)
            staticProvider.URL = null;
    }

    protected override ulong GetAssetTimestamp()
    {
        var assetPath = AssetDatabase.GetAssetPath(Source);

        if (assetPath == "Library/unity default resources" ||
            assetPath == "Resources/unity_builtin_extra")
            return 0;

        if (!string.IsNullOrEmpty(assetPath))
        {
            var importer = AssetImporter.GetAtPath(assetPath);

            if (importer == null)
                throw new Exception($"Could not get importer for asset path: {assetPath}");

            return importer.assetTimeStamp;
        }
        else
        {
            // This is a procedural asset. There's not a nice general way to determine if the asset has changed or not
            // So we just force it to be re-converted every single time
            // TODO!!! Potential ways to determine if asset changed or not? Could be for specific assets, like
            // for textures we could use the image hash potentially. But that might be more expensive.
            return (ulong)DateTime.UtcNow.Ticks;
        }
    }
}
