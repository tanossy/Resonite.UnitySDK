using ResoniteLink;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public class ResoniteLinkWindow : EditorWindow
{
    public enum ConnectionMethod
    {
        AutoDiscovery,
        Manual
    }

    public enum ConnectionState
    {
        Disconnected,
        Connecting,
        Initializing,
        Connected,
    }

    public int Port;

    ConnectionMethod _connectionMethod;

    public ConnectionState State
    {
        get
        {
            if(_linkInterface != null)
            {
                if (_linkInterface.IsConnected)
                {
                    if (_uniqueSessionId == null)
                        return ConnectionState.Initializing;
                    else
                        return ConnectionState.Connected;
                }
                else if (_connecting)
                    return ConnectionState.Connecting;
            }

            return ConnectionState.Disconnected;
        }
    }

    [SerializeField]
    public bool ConvertSkybox = true;

    [SerializeField]
    public bool LogMessageJSON;

    public string UniqueSessionId => _uniqueSessionId;

    public LinkInterface Link => _linkInterface;

    LinkSessionListener _listener;
    List<ResoniteLinkSession> _sessions = new List<ResoniteLinkSession>();

    [SerializeField]
    SceneConverter _converter;

    string _lastConnectionError;
    LinkInterface _linkInterface;
    bool _connecting;

    string _resoniteVersion;
    string _resoniteLinkVersion;
    string _uniqueSessionId;

    string _lastUniqueSessionId;
    int _lastPort;

    [MenuItem("Resonite SDK/Open Resonite SDK Manager")]
    public static void ShowWindow()
    {
        ResoniteLinkWindow window = GetWindow<ResoniteLinkWindow>();
        window.titleContent = new GUIContent("Resonite SDK");
    }

    void OnGUI()
    {
        if(_listener == null)
        {
            _listener = new LinkSessionListener();
            _listener.SessionDiscovered += SessionDiscovered;
            _listener.SessionUpdated += SessionUpdated;
            _listener.SessionClosed += SessionClosed;
            _listener.Start();
        }

        EnsureConverter();

        var connectionMethodNames = Enum.GetNames(typeof(ConnectionMethod));

        GUILayout.Label($"⚠️ CAUTION!!! ⚠️\n\n" +
            $"The SDK is currently in beta and missing a lot of converters.\n" +
            $"If you want a smooth experience, consider giving it a few more weeks until we remove this notice.\n\n" +
            $"Before installing a new version of the SDK, please delete the ResoniteSDK folder first!");

        GUI.enabled = State == ConnectionState.Disconnected;

        _connectionMethod = (ConnectionMethod)GUILayout.SelectionGrid((int)_connectionMethod, 
            connectionMethodNames, connectionMethodNames.Length);

        switch(_connectionMethod)
        {
            case ConnectionMethod.AutoDiscovery:
                _sessions.Clear();
                _listener.GetDiscoveredSessions(_sessions);

                if (_sessions.Count == 0)
                    GUILayout.Label("No ResoniteLink sessions found");
                else
                {
                    foreach(var session in _sessions)
                        if(GUILayout.Button($"Connect to session \"{session.SessionName}\" (port: {session.LinkPort})"))
                        {
                            Port = session.LinkPort;
                            ConnectPressed();
                        }
                }
                break;

            case ConnectionMethod.Manual:
                Port = EditorGUILayout.IntField("Port: ", Port);

                GUI.enabled = State != ConnectionState.Connecting;

                if (GUILayout.Button(ConnectButtonLabel))
                    ConnectPressed();
                break;
        }        

        GUI.enabled = true;
        EditorGUILayout.LabelField($"ResoniteLinkStatus: ", StatusLabel);

        if (!string.IsNullOrEmpty(_lastConnectionError))
            GUILayout.Label($"ERROR: {_lastConnectionError}");

        GUILayout.Space(16);

        // The current scene can only be sent when the realtime mode isn't active
        GUI.enabled = State == ConnectionState.Connected && !_converter.IsRealtimeModeActive;

        ConvertSkybox = GUILayout.Toggle(ConvertSkybox, "Convert Skybox");

        // 2026-08-18: "Force Refresh Generated Lightmaps" and "Send Tonemap Compensation
        // (experimental)" (both overlay additions, not part of vanilla Resonite.UnitySDK — unlike
        // Convert Skybox above, which is upstream) moved to the Lightmap Pipeline panel's
        // "Send-Time Options" section (see LightmapPipelineWindow.cs's DrawSendTimeOptionsSection())
        // per Tanossy's feedback to keep this official-looking panel limited to what's actually
        // original. Their backing state (ConversionPassState.ForceRefreshGeneratedLightmaps /
        // ToneMapCompensationState.Enabled) is unchanged, just no longer written from here.

        if (GUILayout.Button("Send Current Scene"))
            SendCurrentScene();

        GUI.enabled = State == ConnectionState.Connected;

        if (!(_converter?.IsRealtimeModeActive ?? false))
        {
            if (GUILayout.Button("Start Realtime Mode"))
                StartRealtimeMode();
        }
        else
        {
            if (GUILayout.Button("STOP Realtime Mode"))
                StopRealtimeMode();
        }

        GUI.enabled = true;

        // 2026-08-08 (per Tanossy's feedback: "keep all the custom functionality in its own
        // panel"): The "Meshes/Materials/Lightmaps Only" sends, "Retry Missing Asset URLs", and the
        // whole DEBUGGING section (Cleanup tools, Reset conversion state, Log Messages JSON) have
        // been moved out of this official-looking panel and into ResoniteSDKDebugWindow.cs
        // (menu: Resonite SDK/Open Debug Tools). The methods they call have been changed to public
        // so they can be referenced directly from there. Only the always-used controls
        // (Connect, Send Current Scene, Realtime Mode) remain here.
    }

    // Force an update, which should refresh the UI
    void SessionClosed(ResoniteLinkSession obj) => EditorApplication.QueuePlayerLoopUpdate();
    void SessionUpdated(ResoniteLinkSession obj) => EditorApplication.QueuePlayerLoopUpdate();
    void SessionDiscovered(ResoniteLinkSession obj) => EditorApplication.QueuePlayerLoopUpdate();

    void EnsureConverter()
    {
        if (State != ConnectionState.Connected)
            return;

        if(_converter != null)
        {
            if(_converter.IsCorrupted)
            {
                Debug.Log("Conversion state potentially corrupted due to erorrs. Resetting conversion state");
                ResetConversionState();
                return;
            }

            if (UniqueSessionId != _lastUniqueSessionId || _lastPort != Port)
            {
                Debug.Log("Connected to a different ResoniteLink session. Resetting conversion state.\n" +
                    $"PrevID: {_lastUniqueSessionId}, NewID: {UniqueSessionId}, PrevPort: {_lastPort}, NewPort: {Port}");

                ResetConversionState();
                return;
            }
        }

        if (_converter == null)
            _converter = new SceneConverter();

        _converter.EnsureInitialized(this);

        _lastUniqueSessionId = UniqueSessionId;
        _lastPort = Port;
    }

    void SendCurrentScene()
    {
        EnsureConverter();

        _converter.ConvertScene();
    }

    public void SendMeshesOnly()
    {
        EnsureConverter();

        _converter.ConvertMeshesOnly();
    }

    public void SendMaterialsOnly()
    {
        EnsureConverter();

        _converter.ConvertMaterialsOnly();
    }

    public void SendLightmapsOnly()
    {
        EnsureConverter();

        _converter.ConvertLightmapsOnly();
    }

    public void RetryMissingAssetURLs()
    {
        EnsureConverter();

        _converter.RetryMissingAssetURLs();
    }

    void StartRealtimeMode()
    {
        EnsureConverter();

        _converter.StartRealtimeMode();
    }

    void StopRealtimeMode()
    {
        _converter.StopRealtimeMode();
    }

    void ConnectPressed()
    {
        _lastConnectionError = null;

        if (State == ConnectionState.Disconnected)
        {
            // Cleanup any previous interface if it exists
            _linkInterface?.Dispose();

            _resoniteLinkVersion = null;
            _resoniteVersion = null;
            _uniqueSessionId = null;

            _connecting = true;
            _linkInterface = new LinkInterface();

            Task.Run(async () =>
            {
                try
                {
                    await _linkInterface.Connect(new System.Uri($"ws://localhost:{Port}"), System.Threading.CancellationToken.None);

                    var sessionInfo = await _linkInterface.GetSessionData();

                    if (!sessionInfo.Success)
                        throw new System.Exception($"Error getting session info: {sessionInfo.ErrorInfo}");

                    _resoniteLinkVersion = sessionInfo.ResoniteLinkVersion;
                    _resoniteVersion = sessionInfo.ResoniteVersion;

                    _uniqueSessionId = sessionInfo.UniqueSessionId;
                }
                catch(System.Exception ex)
                {
                    _lastConnectionError = ex.Message;

                    // Cleanup
                    _linkInterface.Dispose();
                    _linkInterface = null;
                }
                finally
                {
                    _connecting = false;
                }
            });
        }
        else
        {
            var __linkInterface = _linkInterface;
            _linkInterface = null;

            Task.Run(async () =>
            {
                __linkInterface.Dispose();
            });
        }
    }

    public void CleanupConverters()
    {
        var roots = SceneManager.GetActiveScene().GetRootGameObjects();

        foreach (var root in roots)
            foreach (var converter in root.GetComponentsInChildren<ResoniteComponentConverter>())
                DestroyImmediate(converter);
    }

    public void CleanupReosniteComponents()
    {
        var roots = SceneManager.GetActiveScene().GetRootGameObjects();

        foreach (var root in roots)
            foreach (var component in root.GetComponentsInChildren<ResoniteComponent>())
                DestroyImmediate(component);
    }

    public void ResetConversionState()
    {
        // 2026-07-14 bug fix (per Tanossy's feedback): Previously this method only recreated
        // _converter, while the converters already living under the __UnityAssets / __UnitySkybox
        // roots in the Unity scene (Texture2DConverter/MeshConverter/AudioClipConverter etc. -
        // AssetConversionManager's constructor is designed to "scan and reuse the contents if a
        // root with the same name already exists") were left surviving as-is.
        //
        // Because these converters still held onto the IDs issued by the "previous session", a new
        // session (whether UniqueSessionId changed, or the connection broke and reconnected) would
        // see them as pointing to components that no longer exist, causing update failures like
        // "Component with ID '...' not found". Converters on the scene hierarchy side could also
        // end up duplicated across sessions for the same reason.
        //
        // Fix: when recognizing a new session and resetting, don't just recreate the controller
        // (_converter) - also destroy the full existing set of converter components (both on the
        // scene hierarchy side and the asset side), so the next conversion starts from a
        // completely clean state.
        //
        // 2026-07-15 (fixed after observing frequent "Destroying object multiple times" warnings
        // on real hardware): CleanupReosniteComponents() was originally included here, but it
        // turned out to be entirely redundant. Every ResoniteComponent on the scene hierarchy side
        // is already destroyed via the chain ResoniteComponentConverter.OnDestroy() -> Cleanup() ->
        // DestroyImmediate(Binding) (CleanupConverters()'s DestroyImmediate(converter) triggers
        // this chain every time). The ResoniteComponent(Provider) on the asset side is also swept
        // away as a child at the point where CleanupAssetConversionRoots() destroys
        // __UnityAssets/__UnitySkybox themselves entirely. CleanupReosniteComponents() was
        // independently re-collecting the same components via
        // GetComponentsInChildren<ResoniteComponent>() and trying to DestroyImmediate them again
        // even though they were already destroyed (via the chain / parent destruction), which is
        // what produced the double-destroy warning.
        CleanupConverters();
        CleanupAssetConversionRoots();

        _converter = null;
        EnsureConverter();
    }

    // __UnityAssets / __UnitySkybox are designed to "reuse the contents if a GameObject with the
    // same name already exists" (AssetConversionManager's constructor, SkyboxConverter.EnsureRoot()),
    // so simply removing individual components via CleanupConverters()/CleanupReosniteComponents()
    // isn't enough to prevent the accident where "converters from the previous session get
    // rescanned and resurrected into the empty container" - the root GameObject itself must be
    // destroyed too.
    void CleanupAssetConversionRoots()
    {
        var roots = SceneManager.GetActiveScene().GetRootGameObjects();

        foreach (var root in roots)
        {
            if (root.name == AssetConversionManager.ASSETS_ROOT_NAME ||
                root.name == SkyboxConverter.SKYBOX_ROOT_NAME)
            {
                DestroyImmediate(root);
            }
        }
    }

    string ConnectButtonLabel => State switch
    {
        ConnectionState.Disconnected => "Connect",
        ConnectionState.Connecting => "Connecting...",
        ConnectionState.Initializing => "Initializing...",
        ConnectionState.Connected => "Disconnect",
    };

    string StatusLabel => State switch
    {
        ConnectionState.Disconnected => "Disconnected",
        ConnectionState.Connecting => $"Connecting to port {Port}...",
        ConnectionState.Initializing => $"Initializing session...",
        ConnectionState.Connected => $"Connected on port {Port} (Resonite: {_resoniteVersion})"
    };
}
