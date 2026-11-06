using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Debug = UnityEngine.Debug;

/// <summary>
/// Headset side of the on-demand pipeline: sends a sparse point cloud to the edge server, receives the
/// reconstructed mesh (.glb) and shows it through RuntimeMeshLoader, timing every phase.
///
/// Start the server first, from the repository root:   python -m edge.server
/// Then press Play. To ask again during Play: component menu (three dots) > "Request mesh from edge".
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

    [Tooltip("Point cloud inside Assets/StreamingAssets: text, one 'x y z' per line")]
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
        var baseUrl = serverUrl.TrimEnd('/');
        string label, url;
        byte[] body = null;
        var pointsSent = 0;

        if (source == PointSource.SendPointsFromFile)
        {
            var path = Path.Combine(Application.streamingAssetsPath, pointCloudFile);
            float[] xyz;
            try
            {
                xyz = ReadXyz(path);
            }
            catch (Exception e)
            {
                Debug.LogError($"[EdgeMeshClient] cannot read {path}: {e.Message}");
                return false;
            }
            body = new byte[xyz.Length * sizeof(float)];
            Buffer.BlockCopy(xyz, 0, body, 0, body.Length);  // raw float32, little-endian on PC and Quest
            pointsSent = xyz.Length / 3;
            label = Path.GetFileNameWithoutExtension(pointCloudFile);
            url = $"{baseUrl}/reconstruct?format=xyz&object_id={UnityWebRequest.EscapeURL(label)}";
        }
        else
        {
            label = $"{storedShape}_n{storedShapePoints}";
            url = $"{baseUrl}/shapes/{UnityWebRequest.EscapeURL(storedShape)}/mesh?points={storedShapePoints}";
        }
        var prepareMs = total.Elapsed.TotalMilliseconds;

        using var request = body != null
            ? new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(body) { contentType = "application/octet-stream" },
                downloadHandler = new DownloadHandlerBuffer()
            }
            : UnityWebRequest.Get(url);
        request.timeout = timeoutSeconds;

        Debug.Log($"[EdgeMeshClient] {label}: requesting {url}");
        var roundTrip = Stopwatch.StartNew();
        try
        {
            var op = request.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();
        }
        catch (InvalidOperationException e)  // "Insecure connection not allowed"
        {
            Debug.LogError($"[EdgeMeshClient] {e.Message}\nUnity blocks plain http:// by default: " +
                           "Edit > Project Settings > Player > Other Settings > Allow downloads over HTTP = Always allowed");
            return false;
        }
        roundTrip.Stop();
        if (this == null)  // Play mode stopped while waiting
            return false;

        if (request.result != UnityWebRequest.Result.Success)
        {
            var detail = request.downloadHandler?.text;
            var hint = request.result == UnityWebRequest.Result.ConnectionError
                ? "\nIs the server running? From the repository root: python -m edge.server"
                : "";
            Debug.LogError($"[EdgeMeshClient] {label}: {request.error} {detail}{hint}");
            return false;
        }

        var glb = request.downloadHandler.data;
        var headers = new Dictionary<string, string>(request.GetResponseHeaders() ?? new Dictionary<string, string>(),
                                                     StringComparer.OrdinalIgnoreCase);
        if (!await _loader.LoadFromBytes(glb, label))
            return false;
        total.Stop();

        var t = new Timings
        {
            Label = label,
            Source = source.ToString(),
            Backend = Text(headers, "X-Backend"),
            Model = Text(headers, "X-Model"),
            PointsSent = pointsSent,
            BytesUp = body?.Length ?? 0,
            BytesDown = glb.Length,
            Triangles = _loader.LastTriangles,
            PrepareMs = prepareMs,
            RoundTripMs = roundTrip.Elapsed.TotalMilliseconds,
            ServerMs = Number(headers, "X-Server-Ms"),
            QueueMs = Number(headers, "X-Queue-Ms"),
            InferenceMs = Number(headers, "X-Inference-Ms"),
            LoadMs = _loader.LastLoadMs,
            TotalMs = total.Elapsed.TotalMilliseconds
        };
        t.NetworkMs = t.RoundTripMs - t.ServerMs;  // transfer + HTTP overhead (NaN if the header is missing)

        Debug.Log($"[EdgeMeshClient] {label}: {t.PointsSent} points ({t.BytesUp / 1024f:F1} KB up) -> " +
                  $"{t.Triangles} triangles ({t.BytesDown / 1024f:F1} KB down) | round trip {t.RoundTripMs:F0} ms = " +
                  $"server {t.ServerMs:F0} ms (queue {t.QueueMs:F0}, inference {t.InferenceMs:F0}) + network/HTTP " +
                  $"{t.NetworkMs:F0} ms | glb load {t.LoadMs:F0} ms | total {t.TotalMs:F0} ms [{t.Backend}/{t.Model}]");
        if (logToCsv)
            AppendCsv(t);
        return true;
    }

    // ------------------------------------------------------------------ helpers
    static float[] ReadXyz(string path)
    {
        var values = new List<float>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
                throw new FormatException($"expected 'x y z', got '{line}'");
            // InvariantCulture: on an Italian Windows the default decimal separator is ',' and "0.5" would fail
            for (var i = 0; i < 3; i++)
                values.Add(float.Parse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture));
        }
        if (values.Count == 0)
            throw new FormatException("no points in file");
        return values.ToArray();
    }

    static string Text(Dictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var v) ? v : "";

    static double Number(Dictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var v) &&
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;

    struct Timings
    {
        public string Label, Source, Backend, Model;
        public int PointsSent, BytesUp, BytesDown, Triangles;
        public double PrepareMs, RoundTripMs, ServerMs, QueueMs, InferenceMs, NetworkMs, LoadMs, TotalMs;
    }

    static bool _csvPathLogged;

    static void AppendCsv(Timings t)
    {
        var path = Path.Combine(Application.persistentDataPath, "edge_timings.csv");
        try
        {
            var inv = CultureInfo.InvariantCulture;
            var isNew = !File.Exists(path);
            using var w = new StreamWriter(path, append: true);
            if (isNew)
                w.WriteLine("timestamp,label,source,backend,model,points_sent,bytes_up,bytes_down,triangles," +
                            "prepare_ms,round_trip_ms,server_ms,queue_ms,inference_ms,network_ms,load_ms,total_ms");
            w.WriteLine(string.Join(",",
                DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff", inv), t.Label, t.Source, t.Backend, t.Model,
                t.PointsSent.ToString(inv), t.BytesUp.ToString(inv), t.BytesDown.ToString(inv), t.Triangles.ToString(inv),
                t.PrepareMs.ToString("F1", inv), t.RoundTripMs.ToString("F1", inv), t.ServerMs.ToString("F1", inv),
                t.QueueMs.ToString("F1", inv), t.InferenceMs.ToString("F1", inv), t.NetworkMs.ToString("F1", inv),
                t.LoadMs.ToString("F1", inv), t.TotalMs.ToString("F1", inv)));
            if (!_csvPathLogged)
            {
                Debug.Log($"[EdgeMeshClient] timings appended to {path}");
                _csvPathLogged = true;
            }
        }
        catch (IOException e)
        {
            Debug.LogWarning($"[EdgeMeshClient] cannot write {path}: {e.Message}");
        }
    }
}
