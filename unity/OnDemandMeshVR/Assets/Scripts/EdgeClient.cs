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
        public string Error = "";
        public byte[] Glb;
        public int BytesUp;
        public double RoundTripMs;
        public double ServerMs = double.NaN, QueueMs = double.NaN, InferenceMs = double.NaN;
        public string Backend = "", Model = "";

        /// <summary>Transfer + HTTP overhead + waiting for Unity's main thread.</summary>
        public double NetworkMs => RoundTripMs - ServerMs;
    }

    [Serializable]
    class HealthInfo  // filled by JsonUtility
    {
        public string status = "";
        public string backend = "";
        public string model = "";
    }

    /// <summary>GET /health with a short timeout. Returns (reachable, "backend/model" or the error).</summary>
    public async Task<(bool ok, string info)> HealthAsync(int timeoutSeconds = 3)
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
            return (false, e.Message);
        }
        if (request.result != UnityWebRequest.Result.Success)
            return (false, request.error);
        try
        {
            var h = JsonUtility.FromJson<HealthInfo>(request.downloadHandler.text);
            return (true, $"{h.backend}/{h.model}");
        }
        catch (ArgumentException)
        {
            return (true, "unknown backend");
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

    /// <summary>GET /shapes/{name}/mesh: the cloud is stored on the edge, the headset only names it.</summary>
    public Task<Response> StoredShapeMeshAsync(string shape, int points)
    {
        var url = $"{BaseUrl}/shapes/{UnityWebRequest.EscapeURL(shape)}/mesh?points={points}";
        return SendAsync(UnityWebRequest.Get(url), 0);
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

            if (request.result != UnityWebRequest.Result.Success)
            {
                r.Error = $"{request.error} {request.downloadHandler?.text}";
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
            r.Ok = true;
            return r;
        }
    }

    static double Number(Dictionary<string, string> headers, string name) =>
        headers.TryGetValue(name, out var v) &&
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
}
