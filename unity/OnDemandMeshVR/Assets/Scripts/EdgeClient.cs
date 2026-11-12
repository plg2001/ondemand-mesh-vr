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
/// HTTP client of the edge server (edge/server.py). One instance can be shared by many objects: the server
/// reconstructs one object at a time anyway, the others wait in its queue (reported as QueueMs).
/// </summary>
public class EdgeClient
{
    /// <summary>
    /// Optional file in Application.persistentDataPath whose first line replaces the server address set in the
    /// Inspector. tools/quest/quest.py writes it on the headset, so switching between USB and Wi-Fi needs no rebuild.
    /// </summary>
    public const string ServerOverrideFile = "edge_server.txt";

    public readonly string BaseUrl;
    public readonly int TimeoutSeconds;

    public EdgeClient(string baseUrl, int timeoutSeconds = 600)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        TimeoutSeconds = timeoutSeconds;
    }

    /// <summary>The address to use: the override file if present, otherwise the Inspector value.</summary>
    public static string ResolveUrl(string inspectorUrl)
    {
        var path = Path.Combine(Application.persistentDataPath, ServerOverrideFile);
        try
        {
            if (File.Exists(path))
                foreach (var raw in File.ReadAllLines(path))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("http://") || line.StartsWith("https://"))
                    {
                        Debug.Log($"[EdgeClient] server address from {path}: {line}");
                        return line;
                    }
                }
        }
        catch (IOException e)
        {
            Debug.LogWarning($"[EdgeClient] cannot read {path}: {e.Message}");
        }
        return inspectorUrl;
    }

    public class Response
    {
        public bool Ok;
        public bool ConnectionFailed;  // the server could not be reached at all (off, wrong address, firewall)
        public long StatusCode;        // HTTP status of the answer (0 = no answer)
        public string Error = "";
        public byte[] Glb;
        public int BytesUp;
        public double RoundTripMs;
        public double ServerMs = double.NaN, QueueMs = double.NaN, InferenceMs = double.NaN;
        public string Backend = "", Model = "", ModelLabel = "";

        // accuracy of the mesh against the original one, measured by the server (NaN: object without a reference)
        public double FScore = double.NaN, Precision = double.NaN, Recall = double.NaN, ChamferL2 = double.NaN;
        public double QualityMs = double.NaN;  // time of that evaluation, included in ServerMs

        /// <summary>Transfer + HTTP overhead + waiting for Unity's main thread.</summary>
        public double NetworkMs => RoundTripMs - ServerMs;
    }

    /// <summary>The answer of GET /health (filled by JsonUtility): which model, and inputs for a time estimate.</summary>
    [Serializable]
    public class Health
    {
        public string status = "";
        public string backend = "";
        public string model = "";
        public string label = "";  // model name to show, e.g. "MeshAnything V2" or "convex-hull"
        public float typical_s;    // median server time of the last meshes (mock: its delay); 0 = no idea yet
        public float budget_s;     // the server gives up on a mesh after this many seconds; 0 = never
    }

    /// <summary>GET /health with a short timeout. Returns (reachable, "backend/model" or the error, the answer).</summary>
    public async Task<(bool ok, string info, Health health)> HealthAsync(int timeoutSeconds = 3)
    {
        using var request = UnityWebRequest.Get($"{BaseUrl}/health");
        request.timeout = timeoutSeconds;
        try
        {
            var op = request.SendWebRequest();
            while (!op.isDone)
                await Task.Yield();
        }
        catch (InvalidOperationException e)
        {
            return (false, e.Message, null);
        }
        if (request.result != UnityWebRequest.Result.Success)
            return (false, request.error, null);
        try
        {
            var h = JsonUtility.FromJson<Health>(request.downloadHandler.text);
            return (true, $"{h.backend}/{h.model}", h);
        }
        catch (ArgumentException)
        {
            return (true, "unknown backend", null);
        }
    }

    /// <summary>POST /reconstruct: the headset sends the object's cloud (raw float32 xyz, glTF frame).</summary>
    public Task<Response> ReconstructAsync(byte[] xyzFloat32, string objectId)
    {
        var url = $"{BaseUrl}/reconstruct?format=xyz&object_id={UnityWebRequest.EscapeURL(objectId)}";
        var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
        {
            uploadHandler = new UploadHandlerRaw(xyzFloat32) { contentType = "application/octet-stream" },
            downloadHandler = new DownloadHandlerBuffer()
        };
        return SendAsync(request, xyzFloat32.Length);
    }

    async Task<Response> SendAsync(UnityWebRequest request, int bytesUp)
    {
        var r = new Response { BytesUp = bytesUp };
        using (request)
        {
            request.timeout = TimeoutSeconds;
            var timer = Stopwatch.StartNew();
            try
            {
                var op = request.SendWebRequest();
                while (!op.isDone)
                    await Task.Yield();
            }
            catch (InvalidOperationException e)  // "Insecure connection not allowed"
            {
                r.ConnectionFailed = true;
                r.Error = e.Message + "\nUnity blocks plain http:// by default: Edit > Project Settings > Player > " +
                          "Other Settings > Allow downloads over HTTP = Always allowed";
                return r;
            }
            timer.Stop();
            r.RoundTripMs = timer.Elapsed.TotalMilliseconds;
            r.StatusCode = request.responseCode;

            if (request.result != UnityWebRequest.Result.Success)
            {
                var detail = ErrorDetail(request.downloadHandler?.text);
                r.Error = detail.Length > 0 ? $"HTTP {request.responseCode}: {detail}"
                                            : $"{request.error} {request.downloadHandler?.text}";
                if (request.result == UnityWebRequest.Result.ConnectionError)
                {
                    r.ConnectionFailed = true;
                    r.Error += $"\nIs the server running and reachable at {BaseUrl}? On the PC: start_edge_server.bat " +
                               "(or tools/quest/server_usb.bat / server_wifi.bat for the headset).";
                }
                return r;
            }

            var headers = new Dictionary<string, string>(
                request.GetResponseHeaders() ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            r.Glb = request.downloadHandler.data;
            r.ServerMs = Number(headers, "X-Server-Ms");
            r.QueueMs = Number(headers, "X-Queue-Ms");
            r.InferenceMs = Number(headers, "X-Inference-Ms");
            r.Backend = headers.TryGetValue("X-Backend", out var b) ? b : "";
            r.Model = headers.TryGetValue("X-Model", out var m) ? m : "";
            r.ModelLabel = headers.TryGetValue("X-Model-Label", out var l) ? l : r.Model;
            r.FScore = Number(headers, "X-FScore");
            r.Precision = Number(headers, "X-Precision");
            r.Recall = Number(headers, "X-Recall");
            r.ChamferL2 = Number(headers, "X-Chamfer-L2");
            r.QualityMs = Number(headers, "X-Quality-Ms");
            r.Ok = true;
            return r;
        }
    }

    [Serializable]
    class ErrorBody  // the server's errors are JSON {"detail": "..."} (FastAPI)
    {
        public string detail = "";
    }

    /// <summary>The "detail" of a JSON error answer, "" if the body is something else.</summary>
    static string ErrorDetail(string body)
    {
        if (string.IsNullOrEmpty(body) || !body.TrimStart().StartsWith("{"))
            return "";
        try
        {
            return JsonUtility.FromJson<ErrorBody>(body)?.detail ?? "";
        }
        catch (ArgumentException)
        {
            return "";
        }
    }

    static double Number(Dictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var v) &&
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
}
