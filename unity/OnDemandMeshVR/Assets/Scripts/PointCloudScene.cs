using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Locomotion;
using Debug = UnityEngine.Debug;

/// <summary>
/// Loads a scene built by data/scenes/build_scene.py (StreamingAssets/Scenes/name/scene.json): one child
/// GameObject per object, placed with its pose, drawing the object's sparse cloud as points.
///
/// Mode On Demand (default): every Check Interval the camera view is tested; objects in view that have no mesh are
/// sent to the edge server, one at a time, the one closest to the centre of the view first (Queue Order), and their
/// points are replaced by the mesh. A failed object is asked for again only if that can help (server unreachable or
/// a passing error, with a pause that doubles at every failure); objects the server gave up on (time budget) or
/// refused stay as points. A mesh that stays out of view for Release After Seconds is removed and the object goes
/// back to points; with Cache Meshes the received .glb is kept, so the object is shown again at once when it comes
/// back into view.
/// Mode Reconstruct All On Start: every object, nearest first, regardless of the view.
///
/// Works in the Editor and on Quest (Android). On the headset a status panel below the line of sight shows the
/// server state and the frame rate; the server address can be changed without a rebuild (tools/quest/quest.py).
/// Context menu (three dots of the component, during Play): Reconstruct all objects / Remove all meshes /
/// Retry failed objects / Print memory report.
/// </summary>
public class PointCloudScene : MonoBehaviour
{
    public enum Mode
    {
        OnDemand,              // reconstruct what enters the view, release what leaves it
        ReconstructAllOnStart, // every object, nearest first, no visibility logic
        Manual                 // nothing automatic: use the context menu
    }

    public enum QueueOrder
    {
        CenterOfView,  // the object closest to where the user is looking
        Nearest        // the object closest to the user
    }

    const double MaxRetryPauseSeconds = 300;

    [Header("Scene")]
    [Tooltip("Folder inside Assets/StreamingAssets that contains scene.json")]
    public string sceneFolder = "Scenes/demo_room";

    [Tooltip("Material of the reconstructed meshes: MeshGrey (Render Face = Both)")]
    public Material meshMaterial;

    [Header("Points (placeholder)")]
    [Tooltip("Particle material for the points. Empty = Resources/PointCloud (included in headset builds)")]
    public Material pointMaterial;

    [Tooltip("Point diameter in metres")]
    public float pointSize = 0.01f;

    public Color pointColor = new Color(0.25f, 0.8f, 1f);

    [Header("Edge server")]
    [Tooltip("127.0.0.1 = this PC (or the USB tunnel on the headset). A file edge_server.txt in " +
             "persistentDataPath, written by tools/quest/quest.py, replaces it without a rebuild.")]
    public string serverUrl = "http://127.0.0.1:8000";

    public int timeoutSeconds = 600;

    [Header("Behaviour")]
    public Mode mode = Mode.OnDemand;

    [Tooltip("Which object in view is requested first: the one closest to the centre of the view (where the user " +
             "is looking) or the nearest one")]
    public QueueOrder queueOrder = QueueOrder.CenterOfView;

    [Tooltip("How often the camera view is tested, in seconds")]
    public float checkIntervalSeconds = 0.1f;

    [Tooltip("Metres added around each object when testing the view: requests start slightly earlier")]
    public float viewMargin = 0.1f;

    [Tooltip("Objects farther than this stay as points")]
    public float maxDistance = 15f;

    [Tooltip("A mesh out of view for longer than this is removed and the object goes back to points")]
    public float releaseAfterSeconds = 3f;

    [Tooltip("Keep each received .glb in memory: an object that comes back into view is shown again without a request")]
    public bool cacheMeshes = true;

    [Tooltip("Seconds before asking again for an object after a server error, doubled at every new failure (at " +
             "most 5 minutes). Objects the server gave up on (time budget, HTTP 504) or refused (HTTP 4xx, e.g. " +
             "--objects) are not asked for again: context menu > Retry failed objects")]
    public float retryAfterSeconds = 10f;

    [Tooltip("At start, load and draw a tiny mesh once (invisible): the first real mesh then does not pay glTFast's " +
             "first load and the shader preparation, which on the Quest cost 1-2 s at 50-60 fps")]
    public bool warmUpMeshLoading = true;

    [Header("Controllers")]
    [Tooltip("On the headset, turn off the thumbstick locomotion of the XR rig (move, turn, teleport): a thumbstick " +
             "that drifts would push the user around by itself. Walk and turn physically instead, as in the thesis " +
             "scenario. The rest of the controllers (and the hands) keeps working.")]
    public bool disableStickLocomotion = true;

    [Tooltip("Also in the Editor, to try this behaviour without a headset")]
    public bool disableStickLocomotionInEditor;

    [Header("Headset status panel / logging")]
    [Tooltip("Text panel below the line of sight with server state, objects and frame rate (always on the headset)")]
    public bool showStatusPanel = true;

    [Tooltip("Show the panel also in the Editor (there the Console is usually enough)")]
    public bool statusPanelInEditor;

    [Tooltip("Above each object whose mesh is shown, a small label: model, triangles, time and the accuracy measured " +
             "by the server against the original mesh (F-score, precision, recall, Chamfer)")]
    public bool showObjectLabels = true;

    [Tooltip("Write edge_timings.csv and ondemand_events.csv in Application.persistentDataPath")]
    public bool logToCsv = true;

    readonly List<SceneObject> _objects = new List<SceneObject>();
    readonly Plane[] _planes = new Plane[6];
    SceneManifest _manifest;
    EdgeClient _client;
    SceneObject _inFlight;       // the object whose mesh is being fetched (one at a time)
    double _inFlightSince;       // when its request started (Time.realtimeSinceStartupAsDouble)
    EdgeClient.Health _health;   // last /health answer: model, typical time, time budget
    bool _busy;                  // ReconstructAll running
    bool _loaded;
    float _nextCheck;
    bool _cameraWarned;

    // server state, shown on the panel; requests are sent only while the server answers /health
    bool _serverOk;
    bool _checkingServer;
    int _healthFailures;      // consecutive /health misses
    int _locomotionOff = -1;  // providers turned off by DisableStickLocomotion (-1: left as they are)
    float _nextServerCheck;
    string _serverInfo = "not checked yet";
    string _lastEvent = "";
    HeadsetStatusPanel _panel;
    float _nextPanelUpdate;
    float _fps;

    public IReadOnlyList<SceneObject> Objects => _objects;
    public string EffectiveServerUrl => _client?.BaseUrl ?? serverUrl;

    void Awake()
    {
        // The XR Device Simulator is for the Editor only: on the headset it would fight the real tracking.
        // The clean fix is tagging it EditorOnly (then it is not even built); this is the safety net.
        if (!Application.isEditor)
        {
            var simulator = GameObject.Find("XR Device Simulator");
            if (simulator != null)
            {
                simulator.SetActive(false);
                Debug.LogWarning("[PointCloudScene] XR Device Simulator disabled on the device: tag it EditorOnly");
            }
        }
    }

    async void Start()
    {
        if (disableStickLocomotion && (!Application.isEditor || disableStickLocomotionInEditor))
            DisableStickLocomotion();
        _client = new EdgeClient(EdgeClient.ResolveUrl(serverUrl), timeoutSeconds);
        if (showStatusPanel && (statusPanelInEditor || !Application.isEditor))
        {
            var cam = CurrentCamera();
            if (cam != null)
                _panel = HeadsetStatusPanel.Create(cam.transform);
        }
        _ = CheckServer();
        _loaded = await LoadSceneAsync();
        if (!_loaded || this == null)
            return;
        Debug.Log($"[PointCloudScene] mode: {mode}, queue order: {queueOrder}, edge server: {_client.BaseUrl}");
        if (warmUpMeshLoading)
            await WarmUpMeshLoadingAsync();  // the first request takes seconds anyway: no need to hold it back
        if (this != null && mode == Mode.ReconstructAllOnStart)
            await ReconstructAll();
    }

    /// <summary>
    /// Loads a tiny mesh made by the server's own exporter (WarmUpMesh) with the mesh material and lets it be drawn
    /// for a few frames, 1 m in front of the camera but 0.1 mm wide, so nobody sees it. glTFast's first load and the
    /// GPU's preparation of the material's shader then happen here, during the app's start, instead of with the
    /// first real mesh, where on the Quest they cost 1-2 s at 50-60 fps (and a first load of ~55 ms instead of 28).
    /// </summary>
    async Task WarmUpMeshLoadingAsync()
    {
        var cam = CurrentCamera();
        if (cam == null)
            return;
        var timer = Stopwatch.StartNew();
        var go = new GameObject("WarmUpMesh");
        go.transform.SetParent(cam.transform, false);
        go.transform.localPosition = new Vector3(0f, 0f, 1f);
        go.transform.localScale = Vector3.one * 1e-4f;
        var loader = go.AddComponent<RuntimeMeshLoader>();
        loader.overrideMaterial = meshMaterial;  // the same material as the real meshes, hence the same shader
        var ok = await loader.LoadFromBytes(WarmUpMesh.Glb, "warm-up");
        var until = Time.frameCount + 3;  // the shader is prepared when the mesh is drawn, not when it is loaded
        while (ok && go != null && Time.frameCount < until)
            await Task.Yield();
        if (go != null)
            Destroy(go);
        Debug.Log($"[PointCloudScene] mesh loading warm-up {(ok ? "done" : "FAILED")} in " +
                  $"{timer.Elapsed.TotalMilliseconds:F0} ms (first load {loader.LastLoadMs:F0} ms)");
    }

    // ------------------------------------------------------------------ scene loading
    public async Task<bool> LoadSceneAsync()
    {
        var jsonPath = $"{sceneFolder}/scene.json";
        try
        {
            _manifest = JsonUtility.FromJson<SceneManifest>(await PointCloudIO.ReadStreamingAssetTextAsync(jsonPath));
        }
        catch (Exception e)
        {
            Fail($"cannot read StreamingAssets/{jsonPath}: {e.Message}\n" +
                 "Build the scene first, from the repository root: python data/scenes/build_scene.py");
            return false;
        }
        if (this == null)
            return false;
        if (_manifest?.objects == null || _manifest.objects.Length == 0)
        {
            Fail($"StreamingAssets/{jsonPath} contains no objects");
            return false;
        }
        if (meshMaterial == null)
            Debug.LogWarning("[PointCloudScene] Mesh Material is empty: assign MeshGrey (Render Face = Both)");
        var points = ResolvePointMaterial();

        foreach (var old in _objects)
            if (old != null)
                Destroy(old.gameObject);
        _objects.Clear();

        var total = 0;
        foreach (var entry in _manifest.objects)
        {
            float[] cloud;
            try
            {
                cloud = PointCloudIO.ParseXyz(await PointCloudIO.ReadStreamingAssetTextAsync($"{sceneFolder}/{entry.points}"));
            }
            catch (Exception e)
            {
                Debug.LogError($"[PointCloudScene] {entry.id}: cannot read {entry.points}: {e.Message}");
                continue;
            }
            if (this == null)
                return false;
            var go = new GameObject(entry.id);
            go.transform.SetParent(transform, false);
            var p = Vector3Of(entry.position, Vector3.zero);
            go.transform.localPosition = PointCloudIO.GltfToUnityPosition(p.x, p.y, p.z);
            go.transform.localRotation = entry.rotation != null && entry.rotation.Length == 4
                ? PointCloudIO.GltfToUnityRotation(entry.rotation[0], entry.rotation[1], entry.rotation[2], entry.rotation[3])
                : Quaternion.identity;
            go.transform.localScale = Vector3Of(entry.scale, Vector3.one);
            var obj = go.AddComponent<SceneObject>();
            obj.Init(entry, cloud, meshMaterial, points, pointSize, pointColor, showObjectLabels);
            _objects.Add(obj);
            total += obj.PointCount;
        }

        var t = _manifest.totals;
        Debug.Log($"[PointCloudScene] '{_manifest.name}': {_objects.Count} objects, {total} points " +
                  $"({total * 12 / 1024f:F1} KB as float32). The original meshes would be {t?.source_triangles} " +
                  $"triangles (~{(t?.source_mesh_bytes ?? 0) / 1024f:F0} KB).");
        return _objects.Count > 0;
    }

    Material ResolvePointMaterial()
    {
        if (pointMaterial != null)
            return pointMaterial;
        // Assets/Resources/PointCloud.mat: anything in Resources is always included in the build, shader too
        var material = Resources.Load<Material>("PointCloud");
        if (material != null)
            return material;
        var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Sprites/Default");
        if (shader == null)
        {
            Debug.LogError("[PointCloudScene] no particle shader found: assign Point Material");
            return null;
        }
        // the particle colour (Point Color) multiplies the material colour, which stays white
        return new Material(shader) { name = "PointCloud (runtime)" };
    }

    /// <summary>
    /// Turns off every locomotion provider of the XR Interaction Toolkit (continuous move, snap/continuous turn,
    /// teleport, grab move, climb). They all read the thumbsticks or the hands to move the rig; head tracking, which
    /// is what moves the camera when the user walks or turns, does not depend on them.
    /// </summary>
    void DisableStickLocomotion()
    {
        var providers = FindObjectsByType<LocomotionProvider>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var p in providers)
            p.enabled = false;
        _locomotionOff = providers.Length;
        Debug.Log($"[PointCloudScene] thumbstick locomotion off ({providers.Length} providers disabled): " +
                  "the rig only moves with the head");
    }

    void Fail(string message)
    {
        Debug.LogError("[PointCloudScene] " + message);
        _lastEvent = "<color=#ff6060>" + message.Split('\n')[0] + "</color>";
    }

    // ------------------------------------------------------------------ server state
    async Task CheckServer()
    {
        if (_checkingServer || _client == null)
            return;
        _checkingServer = true;
        try
        {
            var (ok, info, health) = await _client.HealthAsync();
            if (this == null)
                return;
            if (ok)
            {
                if (!_serverOk)
                    Debug.Log($"[PointCloudScene] edge server reachable at {_client.BaseUrl} ({info})");
                _serverOk = true;
                _serverInfo = info;
                _healthFailures = 0;
                if (health != null)
                    _health = health;
            }
            else
            {
                _healthFailures++;
                // Once it has been reachable, one miss is not enough (a busy server may answer late): two in a row.
                if (!_serverOk || _healthFailures >= 2)
                {
                    if (_serverOk || _serverInfo == "not checked yet")
                        Debug.LogWarning($"[PointCloudScene] edge server NOT reachable at {_client.BaseUrl}: {info}");
                    _serverOk = false;
                    _serverInfo = info;
                }
            }
        }
        finally
        {
            _checkingServer = false;
            // while all is well, a light check every 5 s; right after a miss, look again soon; when down, every 3 s
            _nextServerCheck = Time.unscaledTime + (_serverOk ? (_healthFailures > 0 ? 1f : 5f) : 3f);
        }
    }

    // ------------------------------------------------------------------ per frame
    void Update()
    {
        var dt = Time.unscaledDeltaTime;
        if (dt > 0f)
            _fps = _fps <= 0f ? 1f / dt : Mathf.Lerp(_fps, 1f / dt, 0.05f);
        // Poll /health all the time, not only when the server is down: otherwise a server that disappears is noticed
        // only by the next request, which may never come (nothing in view needs a mesh). Not while a request is
        // running: its failure would tell us anyway, and a busy server could answer /health late.
        if (Time.unscaledTime >= _nextServerCheck && _inFlight == null && !_busy)
            _ = CheckServer();
        if (_panel != null && Time.unscaledTime >= _nextPanelUpdate)
        {
            _nextPanelUpdate = Time.unscaledTime + 0.25f;
            _panel.SetText(StatusText());
        }
        if (mode == Mode.OnDemand && _loaded && Time.unscaledTime >= _nextCheck)
        {
            _nextCheck = Time.unscaledTime + checkIntervalSeconds;
            OnDemandStep();
        }
    }

    void OnDemandStep()
    {
        var cam = CurrentCamera();
        if (cam == null)
            return;

        GeometryUtility.CalculateFrustumPlanes(cam, _planes);
        var camPos = cam.transform.position;
        var now = Time.realtimeSinceStartupAsDouble;
        foreach (var o in _objects)
        {
            if (o == null)
                continue;
            var b = o.WorldBounds;
            b.Expand(2f * viewMargin);
            var visible = Vector3.Distance(camPos, b.center) <= maxDistance && GeometryUtility.TestPlanesAABB(_planes, b);
            if (visible)
            {
                // A waiting period starts when the object enters the view with none pending, or after it was away
                // for a while. Turning the camera away and back for a moment must not restart the clock, or the
                // measured wait would be shorter than what the user really experienced; nor must looking away while
                // its request is running (with a real model that takes minutes, the user does look around).
                var requestRunning = o.state == SceneObject.State.Requesting;
                if (!o.inView && (o.VisibleSince < 0 || (!requestRunning && now - o.LastSeen > releaseAfterSeconds)))
                    o.VisibleSince = now;
                o.LastSeen = now;
            }
            o.inView = visible;
            if (!visible && o.state == SceneObject.State.MeshReady && now - o.LastSeen > releaseAfterSeconds)
            {
                o.RemoveMesh();
                o.VisibleSince = -1;
                Debug.Log($"[PointCloudScene] {o.objectId}: out of view for {releaseAfterSeconds:F0} s, mesh removed " +
                          $"(back to points){(o.HasCachedMesh ? ", glb kept in cache" : "")}");
            }
        }

        // A mesh already in memory needs no network: show it at once, even while a request is running.
        if (cacheMeshes)
            foreach (var o in _objects)
                if (o != null && o.inView && o.state == SceneObject.State.PointsOnly && o.HasCachedMesh)
                    _ = ServeFromCache(o);  // sets the state synchronously, so it is not picked up twice

        if (_inFlight == null && !_busy && _serverOk)
        {
            var next = PickNext(cam.transform, now);
            if (next != null)
                _ = Serve(next);
        }
    }

    Camera CurrentCamera()
    {
        var cam = Camera.main;
        if (cam == null)
            cam = FindFirstObjectByType<Camera>();
        if (cam == null && !_cameraWarned)
        {
            Debug.LogWarning("[PointCloudScene] no camera found: on-demand needs a camera tagged MainCamera");
            _cameraWarned = true;
        }
        return cam;
    }

    /// <summary>
    /// The object in view to ask for next: the one closest to the centre of the view or, with Queue Order = Nearest,
    /// the nearest one. A failed object competes again only when asking can help and its pause is over: otherwise a
    /// near object that the server cannot finish would be asked for forever and the others would never get a turn.
    /// </summary>
    SceneObject PickNext(Transform cam, double now)
    {
        SceneObject best = null;
        var bestScore = float.MaxValue;
        foreach (var o in _objects)
        {
            if (o == null || !o.inView)
                continue;
            var needsMesh = o.state == SceneObject.State.PointsOnly ||
                            (o.state == SceneObject.State.Failed && o.WillRetry && now - o.FailedAt > RetryPause(o));
            if (!needsMesh)
                continue;
            var toObject = o.WorldBounds.center - cam.position;
            var score = queueOrder == QueueOrder.CenterOfView ? Vector3.Angle(cam.forward, toObject) : toObject.magnitude;
            if (score < bestScore)
            {
                bestScore = score;
                best = o;
            }
        }
        return best;
    }

    /// <summary>Seconds to wait before asking again for a failed object (meaningful only when it WillRetry).</summary>
    double RetryPause(SceneObject o) =>
        o.LastFailure == SceneObject.Failure.Connection
            ? retryAfterSeconds  // requests were paused until /health answered: the server is back, ask again
            : Math.Min(retryAfterSeconds * Math.Pow(2, Math.Max(0, o.FailedAttempts - 1)), MaxRetryPauseSeconds);

    /// <summary>Asks the edge for the object's mesh. One at a time: the others wait for their turn.</summary>
    async Task Serve(SceneObject o)
    {
        _inFlight = o;
        _inFlightSince = Time.realtimeSinceStartupAsDouble;
        var durationKey = DurationKey(o);  // fixed now: the server's model cannot change during the request
        try
        {
            var waitedMs = WaitedMs(o);
            var row = await o.ReconstructAsync(_client, "on-demand", cacheMeshes);
            if (this == null || o == null)
                return;
            if (row == null)
            {
                if (o.LastFailure == SceneObject.Failure.Connection)
                {
                    _serverOk = false;  // stop sending until /health answers again
                    _serverInfo = o.LastError.Split('\n')[0];
                }
                if (o.NotServedByChoice)
                    return;  // the server serves only some objects (--objects): counted as "not served", no alarm
                var next = o.WillRetry ? $"retry in {RetryPause(o):F0} s" : $"{o.LastFailure}, not retried";
                _lastEvent = $"<color=#ff6060>{o.objectId}: {o.LastError.Split('\n')[0]}</color> ({next})";
                return;
            }
            if (durationKey != null)  // the estimate for the next time this object is asked to this model
            {
                PlayerPrefs.SetFloat(durationKey, (float)(row.Value.TotalMs / 1000.0));
                PlayerPrefs.Save();
            }
            LogOnDemand(o, row, waitedMs);
        }
        finally
        {
            _inFlight = null;
        }
    }

    /// <summary>Shows a mesh kept in memory: no network, so it does not wait for the edge request in flight.</summary>
    async Task ServeFromCache(SceneObject o)
    {
        var waitedMs = WaitedMs(o);
        if (!await o.ShowCachedMeshAsync())
            return;
        if (this == null || o == null)
            return;
        LogOnDemand(o, null, waitedMs);
    }

    /// <summary>Time since the object started waiting for a mesh (in view without one).</summary>
    static double WaitedMs(SceneObject o) =>
        o.VisibleSince >= 0 ? (Time.realtimeSinceStartupAsDouble - o.VisibleSince) * 1e3 : 0;

    /// <summary>Console line, panel line and CSV rows for one object that has just got its mesh (row == null: cache).</summary>
    void LogOnDemand(SceneObject o, EdgeTimingLog.Row? row, double waitedMs)
    {
        var fromCache = row == null;
        var visibleToMeshMs = o.VisibleSince >= 0 ? (Time.realtimeSinceStartupAsDouble - o.VisibleSince) * 1e3 : 0;
        o.VisibleSince = -1;  // served: the next waiting period starts the next time it needs a mesh
        var inView = _objects.Count(x => x != null && x.inView);
        var shown = _objects.Count(x => x != null && x.state == SceneObject.State.MeshReady);
        var how = fromCache ? "from the glb cache, no request to the edge" : EdgeTimingLog.Describe(row.Value);
        Debug.Log($"[PointCloudScene] {o.objectId}: in view -> mesh shown in {visibleToMeshMs:F0} ms " +
                  $"(waited {waitedMs:F0} ms before its request) | {how} | meshes shown {shown}/{_objects.Count}");
        _lastEvent = fromCache
            ? $"{o.objectId}: {visibleToMeshMs:F0} ms (cache)"
            : $"{o.objectId}: {visibleToMeshMs / 1000:F2} s (round trip {row.Value.RoundTripMs:F0} ms, " +
              $"network {row.Value.NetworkMs:F0} ms)";
        if (!logToCsv)
            return;
        if (row != null)
            EdgeTimingLog.Append(row.Value);
        EdgeTimingLog.AppendOnDemand(new EdgeTimingLog.OnDemandEvent
        {
            ObjectId = o.objectId, Source = fromCache ? "cache" : "edge",
            VisibleToMeshMs = visibleToMeshMs, WaitedMs = waitedMs,
            RoundTripMs = row?.RoundTripMs ?? 0, ServerMs = row?.ServerMs ?? 0, LoadMs = o.LastLoadMs,
            Triangles = o.MeshTriangles, BytesDown = row?.BytesDown ?? 0,
            ObjectsInView = inView, MeshesShown = shown
        });
    }

    string StatusText()
    {
        var sb = new StringBuilder();
        sb.Append($"<b>On-demand mesh</b>   {_fps:F0} fps   mode {mode}   " +
                  $"sticks {(_locomotionOff >= 0 ? "off" : "on")}\n");
        sb.Append(_serverOk
            ? $"Edge <color=#70ff70>OK</color> {EffectiveServerUrl} ({_serverInfo})\n"
            : $"Edge <color=#ff6060>NOT REACHABLE</color> {EffectiveServerUrl}\n<size=80%>{_serverInfo}</size>\n");
        if (_loaded)
        {
            var inView = _objects.Count(o => o != null && o.inView);
            var shown = _objects.Count(o => o != null && o.state == SceneObject.State.MeshReady);
            var givenUp = _objects.Count(o => o != null && o.state == SceneObject.State.Failed && !o.WillRetry);
            var waiting = _objects.Count(o => o != null && o.inView && o.state != SceneObject.State.MeshReady &&
                                              !(o.state == SceneObject.State.Failed && !o.WillRetry));
            sb.Append($"Objects {_objects.Count} | in view {inView} | meshes {shown} | waiting {waiting}");
            if (givenUp > 0)
                sb.Append($" | not served {givenUp}");
            sb.Append('\n');
            if (_inFlight != null)
                sb.Append(InFlightText()).Append('\n');
        }
        if (_lastEvent.Length > 0)
            sb.Append($"Last: {_lastEvent}");
        return sb.ToString();
    }

    /// <summary>
    /// The panel line of the request in flight: which object, with which model, for how long, and an estimate of the
    /// time left. The estimate is how long the same object took the last time with the same model (remembered across
    /// runs) or, if never asked, the server's typical time (the median of its last meshes, or the mock's delay).
    /// </summary>
    string InFlightText()
    {
        var o = _inFlight;
        var elapsed = Time.realtimeSinceStartupAsDouble - _inFlightSince;
        var model = _health != null && _health.label.Length > 0 ? _health.label : "edge";
        var sb = new StringBuilder($"<color=#ffd760>Reconstructing <b>{o.objectId}</b></color> ({model}): {elapsed:F0} s");
        var (estimate, source) = EstimateSeconds(o);
        if (estimate <= 0)
            sb.Append(", no estimate yet");
        else if (elapsed < estimate)
            sb.Append($", about {Math.Ceiling(estimate - elapsed):F0} s left ({source} {estimate:F0} s)");
        else
            sb.Append($", longer than {source} ({estimate:F0} s)");
        if (_health != null && _health.budget_s > 0)
            sb.Append($", gives up at {_health.budget_s:F0} s");
        return sb.ToString();
    }

    (double seconds, string source) EstimateSeconds(SceneObject o)
    {
        var key = DurationKey(o);
        if (key != null && PlayerPrefs.HasKey(key))
            return (PlayerPrefs.GetFloat(key), "last time");
        if (_health != null && _health.typical_s > 0)
            return (_health.typical_s, "typical");
        return (0, "");
    }

    /// <summary>PlayerPrefs key of how long this object took with the server's current model (null: model unknown).</summary>
    string DurationKey(SceneObject o) =>
        _health == null || _health.model.Length == 0 ? null : $"edge_seconds/{_health.backend}/{_health.model}/{o.objectId}";

    // ------------------------------------------------------------------ manual / reconstruct-all
    [ContextMenu("Reconstruct all objects")]
    void ReconstructAllFromMenu() => _ = ReconstructAll();

    [ContextMenu("Remove all meshes")]
    void RemoveAllMeshes()
    {
        foreach (var o in _objects)
            if (o != null)
                o.RemoveMesh();
    }

    [ContextMenu("Retry failed objects")]
    void RetryFailedObjects()
    {
        var n = 0;
        foreach (var o in _objects)
            if (o != null && o.state == SceneObject.State.Failed)
            {
                o.ResetFailure();
                n++;
            }
        Debug.Log($"[PointCloudScene] {n} failed objects will be asked for again");
    }

    [ContextMenu("Print memory report")]
    public void PrintMemoryReport()
    {
        int inView = 0, meshes = 0, triangles = 0, vertices = 0, points = 0;
        long cache = 0;
        foreach (var o in _objects)
        {
            if (o == null)
                continue;
            points += o.PointCount;
            cache += o.CachedBytes;
            if (o.inView)
                inView++;
            if (o.state != SceneObject.State.MeshReady)
                continue;
            meshes++;
            triangles += o.MeshTriangles;
            vertices += o.MeshVertices;
        }
        Debug.Log($"[PointCloudScene] memory: {_objects.Count} objects, {inView} in view, {meshes} meshes shown " +
                  $"({triangles} triangles ~{(vertices * 12 + triangles * 12) / 1024f:F1} KB) | points " +
                  $"{points * 12 / 1024f:F1} KB | glb cache {cache / 1024f:F1} KB");
    }

    /// <summary>Requests every object without a mesh, nearest to the camera first, one at a time.</summary>
    public async Task ReconstructAll()
    {
        if (_busy || _inFlight != null)
        {
            Debug.LogWarning("[PointCloudScene] a reconstruction is already running");
            return;
        }
        _busy = true;
        try
        {
            var cam = CurrentCamera();
            var from = cam != null ? cam.transform.position : transform.position;
            var order = _objects.Where(o => o != null && o.state != SceneObject.State.MeshReady)
                                .OrderBy(o => Vector3.Distance(from, o.transform.position)).ToList();
            var timer = Stopwatch.StartNew();
            var done = 0;
            foreach (var obj in order)
            {
                if (this == null || obj == null)
                    return;
                var row = await obj.ReconstructAsync(_client, "scene", cacheMeshes);
                if (row == null)
                    continue;
                if (obj != null)  // in On Demand mode an object out of view is released only after the usual delay
                    obj.LastSeen = Time.realtimeSinceStartupAsDouble;
                done++;
                Debug.Log("[PointCloudScene] " + EdgeTimingLog.Describe(row.Value));
                if (logToCsv)
                    EdgeTimingLog.Append(row.Value);
            }
            Debug.Log($"[PointCloudScene] reconstructed {done}/{order.Count} objects in {timer.Elapsed.TotalSeconds:F1} s");
            PrintMemoryReport();
        }
        finally
        {
            _busy = false;
        }
    }

    static Vector3 Vector3Of(float[] a, Vector3 fallback) =>
        a != null && a.Length == 3 ? new Vector3(a[0], a[1], a[2]) : fallback;
}
