using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Single-object test of the edge pipeline: sends one sparse point cloud to the edge server, receives the
/// reconstructed mesh (.glb) and shows it through RuntimeMeshLoader, timing every phase.
/// For a whole scene of objects use PointCloudScene instead.
///
/// Start the server first (double-click start_edge_server.bat), then press Play.
/// To ask again during Play: component menu (three dots) > "Request mesh from edge".
///
/// Points travel in the frame of the files in data/inputs (right-handed, +Y up, the glTF frame); the mesh
/// comes back in the same frame and glTFast converts it to Unity's frame.
/// </summary>
[RequireComponent(typeof(RuntimeMeshLoader))]
public class EdgeMeshClient : MonoBehaviour
{
    public enum PointSource
    {
        SendPointsFromFile,   // the headset holds the cloud and uploads it      -> POST /reconstruct
        UseCloudStoredOnEdge  // the edge holds the scene, ask for it by name    -> GET /shapes/{name}/mesh
    }

    [Header("Edge server")]
    [Tooltip("127.0.0.1 = server on this PC. For a Quest on Wi-Fi: the PC's LAN address, server started with --host 0.0.0.0")]
    public string serverUrl = "http://127.0.0.1:8000";

    [Tooltip("Seconds before giving up. A real model on a weak PC can take minutes.")]
    public int timeoutSeconds = 600;

    [Header("What to reconstruct")]
    public PointSource source = PointSource.SendPointsFromFile;

    [Tooltip("Point cloud inside Assets/StreamingAssets, including the folder: e.g. PointClouds/box_n2048.xyz")]
    public string pointCloudFile = "PointClouds/box_n2048.xyz";

    [Tooltip("Name of a cloud stored on the edge (open http://127.0.0.1:8000/shapes to list them)")]
    public string storedShape = "box";

    public int storedShapePoints = 2048;

    [Header("When / logging")]
    public bool requestOnStart = true;

    [Tooltip("Append one row per request to edge_timings.csv in Application.persistentDataPath")]
    public bool logToCsv = true;

    RuntimeMeshLoader _loader;
    bool _busy;

    void Awake()
    {
        _loader = GetComponent<RuntimeMeshLoader>();
        _loader.loadOnStart = false;  // here the mesh must come from the server, not from the local test file
    }

    async void Start()
    {
        if (requestOnStart)
            await RequestMesh();
    }

    [ContextMenu("Request mesh from edge")]
    void RequestFromInspector() => _ = RequestMesh();

    public async Task<bool> RequestMesh()
    {
        if (_busy)
        {
            Debug.LogWarning("[EdgeMeshClient] a request is already running");
            return false;
        }
        _busy = true;
        try
        {
            return await RequestMeshInternal();
        }
        finally
        {
            _busy = false;
        }
    }

    async Task<bool> RequestMeshInternal()
    {
        var total = Stopwatch.StartNew();
        var client = new EdgeClient(EdgeClient.ResolveUrl(serverUrl), timeoutSeconds);
        EdgeClient.Response response;
        string label;
        var pointsSent = 0;
        double prepareMs;

        if (source == PointSource.SendPointsFromFile)
        {
            float[] xyz;
            try
            {
                xyz = PointCloudIO.ParseXyz(await PointCloudIO.ReadStreamingAssetTextAsync(pointCloudFile));
            }
            catch (Exception e)
            {
                Debug.LogError($"[EdgeMeshClient] cannot read StreamingAssets/{pointCloudFile}: {e.Message}\n" +
                               "Point Cloud File is relative to Assets/StreamingAssets, e.g. PointClouds/box_n2048.xyz");
                return false;
            }
            if (this == null)
                return false;
            var body = PointCloudIO.ToBytes(xyz);
            pointsSent = xyz.Length / 3;
            label = Path.GetFileNameWithoutExtension(pointCloudFile);
            prepareMs = total.Elapsed.TotalMilliseconds;
            Debug.Log($"[EdgeMeshClient] {label}: sending {pointsSent} points to {client.BaseUrl}");
            response = await client.ReconstructAsync(body, label);
        }
        else
        {
            label = $"{storedShape}_n{storedShapePoints}";
            prepareMs = total.Elapsed.TotalMilliseconds;
            Debug.Log($"[EdgeMeshClient] {label}: asking {client.BaseUrl} for a cloud stored on the edge");
            response = await client.StoredShapeMeshAsync(storedShape, storedShapePoints);
        }
        if (this == null)  // Play mode stopped while waiting
            return false;
        if (!response.Ok)
        {
            Debug.LogError($"[EdgeMeshClient] {label}: {response.Error}");
            return false;
        }
        if (!await _loader.LoadFromBytes(response.Glb, label))
            return false;
        total.Stop();

        var row = EdgeTimingLog.From(label, source.ToString(), pointsSent, response, _loader.LastTriangles,
                                     _loader.LastLoadMs, prepareMs, total.Elapsed.TotalMilliseconds);
        Debug.Log("[EdgeMeshClient] " + EdgeTimingLog.Describe(row));
        if (logToCsv)
            EdgeTimingLog.Append(row);
        return true;
    }
}
