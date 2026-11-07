using System;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>Per-request timings of the headset side: one Console line and one CSV row per reconstruction.</summary>
public static class EdgeTimingLog
{
    public struct Row
    {
        public string Label, Source, Backend, Model;
        public int PointsSent, BytesUp, BytesDown, Triangles;
        public double PrepareMs, RoundTripMs, ServerMs, QueueMs, InferenceMs, NetworkMs, LoadMs, TotalMs;
    }

    public static Row From(string label, string source, int pointsSent, EdgeClient.Response r, int triangles,
                           double loadMs, double prepareMs, double totalMs) => new Row
    {
        Label = label, Source = source, Backend = r.Backend, Model = r.Model,
        PointsSent = pointsSent, BytesUp = r.BytesUp, BytesDown = r.Glb?.Length ?? 0, Triangles = triangles,
        PrepareMs = prepareMs, RoundTripMs = r.RoundTripMs, ServerMs = r.ServerMs, QueueMs = r.QueueMs,
        InferenceMs = r.InferenceMs, NetworkMs = r.NetworkMs, LoadMs = loadMs, TotalMs = totalMs
    };

    public static string Describe(Row t) =>
        $"{t.Label}: {t.PointsSent} points ({t.BytesUp / 1024f:F1} KB up) -> {t.Triangles} triangles " +
        $"({t.BytesDown / 1024f:F1} KB down) | round trip {t.RoundTripMs:F0} ms = server {t.ServerMs:F0} ms " +
        $"(queue {t.QueueMs:F0}, inference {t.InferenceMs:F0}) + network/HTTP {t.NetworkMs:F0} ms | " +
        $"glb load {t.LoadMs:F0} ms | total {t.TotalMs:F0} ms [{t.Backend}/{t.Model}]";

    static bool _pathLogged;

    /// <summary>Appends to edge_timings.csv in Application.persistentDataPath (path printed once).</summary>
    public static void Append(Row t)
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
                t.PointsSent.ToString(inv), t.BytesUp.ToString(inv), t.BytesDown.ToString(inv),
                t.Triangles.ToString(inv), t.PrepareMs.ToString("F1", inv), t.RoundTripMs.ToString("F1", inv),
                t.ServerMs.ToString("F1", inv), t.QueueMs.ToString("F1", inv), t.InferenceMs.ToString("F1", inv),
                t.NetworkMs.ToString("F1", inv), t.LoadMs.ToString("F1", inv), t.TotalMs.ToString("F1", inv)));
            if (!_pathLogged)
            {
                Debug.Log($"[EdgeTimingLog] timings appended to {path}");
                _pathLogged = true;
            }
        }
        catch (IOException e)
        {
            Debug.LogWarning($"[EdgeTimingLog] cannot write {path}: {e.Message}");
        }
    }
}
