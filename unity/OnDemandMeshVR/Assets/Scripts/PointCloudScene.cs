using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Loads a scene built by data/scenes/build_scene.py (StreamingAssets/Scenes/name/scene.json): one child
/// GameObject per object, placed with its pose, drawing the object's sparse cloud as points.
///
/// Mode On Demand (default): every Check Interval the camera view is tested; objects in view that have no mesh are
/// sent to the edge server, one at a time, nearest first, and their points are replaced by the mesh. A mesh that
/// stays out of view for Release After Seconds is removed and the object goes back to points; with Cache Meshes
/// the received .glb is kept, so the object is shown again at once when it comes back into view.
/// Mode Reconstruct All On Start: every object, nearest first, regardless of the view.
///
/// Context menu (three dots of the component, during Play): Reconstruct all objects / Remove all meshes /
/// Print memory report.
/// </summary>
public class PointCloudScene : MonoBehaviour
{
    public enum Mode
    {
        OnDemand,              // reconstruct what enters the view, release what leaves it
        ReconstructAllOnStart, // every object, nearest first, no visibility logic
        Manual                 // nothing automatic: use the context menu
    }

    [Header("Scene")]
    [Tooltip("Folder inside Assets/StreamingAssets that contains scene.json")]
    public string sceneFolder = "Scenes/demo_room";

    [Tooltip("Material of the reconstructed meshes: MeshGrey (Render Face = Both)")]
    public Material meshMaterial;

    [Header("Points (placeholder)")]
    [Tooltip("Particle material for the points. Empty = URP Particles/Unlit created at runtime: fine in the " +
             "Editor, but assign a material for headset builds (Unity may strip unused shaders)")]
    public Material pointMaterial;

    [Tooltip("Point diameter in metres")]
    public float pointSize = 0.01f;

    public Color pointColor = new Color(0.25f, 0.8f, 1f);

    [Header("Edge server")]
    public string serverUrl = "http://127.0.0.1:8000";
    public int timeoutSeconds = 600;

    [Header("Behaviour")]
    public Mode mode = Mode.OnDemand;

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

    [Tooltip("Seconds before retrying an object whose reconstruction failed")]
    public float retryAfterSeconds = 10f;

    [Header("Logging")]
    [Tooltip("Write edge_timings.csv and ondemand_events.csv in Application.persistentDataPath")]
    public bool logToCsv = true;

    readonly List<SceneObject> _objects = new List<SceneObject>();
    readonly Plane[] _planes = new Plane[6];
    SceneManifest _manifest;
    EdgeClient _client;
    SceneObject _inFlight;       // the object whose mesh is being fetched (one at a time)
    bool _busy;                  // ReconstructAll running
    float _nextCheck;
    bool _cameraWarned;

    public IReadOnlyList<SceneObject> Objects => _objects;
    EdgeClient Client => _client ??= new EdgeClient(serverUrl, timeoutSeconds);

    async void Start()
    {
        if (!LoadScene())
            return;
        Debug.Log($"[PointCloudScene] mode: {mode}");
        if (mode == Mode.ReconstructAllOnStart)
            await ReconstructAll();
    }

    // ------------------------------------------------------------------ scene loading
    public bool LoadScene()
    {
        var folder = Path.Combine(Application.streamingAssetsPath, sceneFolder);
        var path = Path.Combine(folder, "scene.json");
        try
        {
            _manifest = JsonUtility.FromJson<SceneManifest>(File.ReadAllText(path));
        }
        catch (Exception e)
        {
            Debug.LogError($"[PointCloudScene] cannot read {path}: {e.Message}\n" +
                           "Build the scene first, from the repository root: python data/scenes/build_scene.py");
            return false;
        }
        if (_manifest?.objects == null || _manifest.objects.Length == 0)
        {
            Debug.LogError($"[PointCloudScene] {path} contains no objects");
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
                cloud = PointCloudIO.ReadXyz(Path.Combine(folder, entry.points));
            }
            catch (Exception e)
            {
                Debug.LogError($"[PointCloudScene] {entry.id}: cannot read {entry.points}: {e.Message}");
                continue;
            }
            var go = new GameObject(entry.id);
            go.transform.SetParent(transform, false);
            var p = Vector3Of(entry.position, Vector3.zero);
            go.transform.localPosition = PointCloudIO.GltfToUnityPosition(p.x, p.y, p.z);
            go.transform.localRotation = entry.rotation != null && entry.rotation.Length == 4
                ? PointCloudIO.GltfToUnityRotation(entry.rotation[0], entry.rotation[1], entry.rotation[2], entry.rotation[3])
                : Quaternion.identity;
            go.transform.localScale = Vector3Of(entry.scale, Vector3.one);
            var obj = go.AddComponent<SceneObject>();
            obj.Init(entry, cloud, meshMaterial, points, pointSize, pointColor);
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
        var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Sprites/Default");
        if (shader == null)
        {
            Debug.LogError("[PointCloudScene] no particle shader found: assign Point Material");
            return null;
        }
        // the particle colour (Point Color) multiplies the material colour, which stays white
        return new Material(shader) { name = "PointCloud (runtime)" };
    }

    // ------------------------------------------------------------------ on demand
    void Update()
    {
        if (mode != Mode.OnDemand || _objects.Count == 0 || Time.unscaledTime < _nextCheck)
            return;
        _nextCheck = Time.unscaledTime + checkIntervalSeconds;
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
                // measured wait would be shorter than what the user really experienced.
                if (!o.inView && (o.VisibleSince < 0 || now - o.LastSeen > releaseAfterSeconds))
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

        if (_inFlight == null && !_busy)
        {
            var next = PickNext(camPos, now);
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

    /// <summary>Nearest object in view that still needs a mesh (failed ones are retried after a pause).</summary>
    SceneObject PickNext(Vector3 camPos, double now)
    {
        SceneObject best = null;
        var bestDistance = float.MaxValue;
        foreach (var o in _objects)
        {
            if (o == null || !o.inView)
                continue;
            var needsMesh = o.state == SceneObject.State.PointsOnly ||
                            (o.state == SceneObject.State.Failed && now - o.FailedAt > retryAfterSeconds);
            if (!needsMesh)
                continue;
            var d = Vector3.Distance(camPos, o.transform.position);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = o;
            }
        }
        return best;
    }

    /// <summary>Asks the edge for the object's mesh. One at a time: the others wait for their turn.</summary>
    async Task Serve(SceneObject o)
    {
        _inFlight = o;
        try
        {
            var waitedMs = WaitedMs(o);
            var row = await o.ReconstructAsync(Client, "on-demand", cacheMeshes);
            if (row == null)
            {
                if (o != null)
                    o.FailedAt = Time.realtimeSinceStartupAsDouble;
                return;
            }
            if (this == null || o == null)
                return;
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
        {
            if (o != null)
                o.FailedAt = Time.realtimeSinceStartupAsDouble;
            return;
        }
        if (this == null || o == null)
            return;
        LogOnDemand(o, null, waitedMs);
    }

    /// <summary>Time since the object started waiting for a mesh (in view without one).</summary>
    static double WaitedMs(SceneObject o) =>
        o.VisibleSince >= 0 ? (Time.realtimeSinceStartupAsDouble - o.VisibleSince) * 1e3 : 0;

    /// <summary>Console line + CSV rows for one object that has just got its mesh. row == null: from the cache.</summary>
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
                var row = await obj.ReconstructAsync(Client, "scene", cacheMeshes);
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
