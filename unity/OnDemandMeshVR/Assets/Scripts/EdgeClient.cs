using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using UnityEngine.Networking;

/// <summary>
/// HTTP client of the edge server (edge/server.py). One instance can be shared by many objects: the server
/// reconstructs one object at a time anyway, the others wait in its queue (reported as QueueMs).
/// </summary>
public class EdgeClient
{
    public readonly string BaseUrl;
    public readonly int TimeoutSeconds;

    public EdgeClient(string baseUrl, int timeoutSeconds = 600)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        TimeoutSeconds = timeoutSeconds;
    }

    public class Response
    {
        public bool Ok;
        public string Error = "";
        public byte[] Glb;
        public int BytesUp;
        public double RoundTripMs;
        public double ServerMs = double.NaN, QueueMs = double.NaN, InferenceMs = double.NaN;
        public string Backend = "", Model = "";

        /// <summary>Transfer + HTTP overhead + waiting for Unity's main thread.</summary>
        public double NetworkMs => RoundTripMs - ServerMs;
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
                    r.Error += "\nIs the server running? Double-click start_edge_server.bat in the repository folder.";
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
