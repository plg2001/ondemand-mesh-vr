using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Point cloud files, the edge server's wire format and the glTF -> Unity frame conversion.
/// glTF (and the Python side) is right-handed, Unity is left-handed: glTFast mirrors the X axis, and so do we,
/// so that clouds, poses and the meshes coming back from the server all agree.
/// </summary>
public static class PointCloudIO
{
    /// <summary>
    /// Reads a text file from StreamingAssets. On PC it is a plain folder; on Android (Quest) the files are packed
    /// inside the .apk and can only be read with UnityWebRequest.
    /// </summary>
    public static async Task<string> ReadStreamingAssetTextAsync(string relativePath)
    {
        var path = Path.Combine(Application.streamingAssetsPath, relativePath).Replace('\\', '/');
        if (!path.Contains("://"))
            return File.ReadAllText(path);
        using var request = UnityWebRequest.Get(path);
        var op = request.SendWebRequest();
        while (!op.isDone)
            await Task.Yield();
        if (request.result != UnityWebRequest.Result.Success)
            throw new IOException($"{path}: {request.error}");
        return request.downloadHandler.text;
    }

    /// <summary>Parses .xyz text ("x y z" per line) into a flat array [x0, y0, z0, x1, ...].</summary>
    public static float[] ParseXyz(string text)
    {
        var values = new List<float>();
        foreach (var raw in text.Split('\n'))
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

    /// <summary>Reads an .xyz file from a plain path (PC only; use ReadStreamingAssetTextAsync + ParseXyz on Quest).</summary>
    public static float[] ReadXyz(string path) => ParseXyz(File.ReadAllText(path));

    /// <summary>Raw float32 bytes, little-endian on PC and Quest: the request body of POST /reconstruct.</summary>
    public static byte[] ToBytes(float[] xyz)
    {
        var bytes = new byte[xyz.Length * sizeof(float)];
        Buffer.BlockCopy(xyz, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static Vector3 GltfToUnityPosition(float x, float y, float z) => new Vector3(-x, y, z);

    public static Quaternion GltfToUnityRotation(float x, float y, float z, float w) => new Quaternion(x, -y, -z, w);

    /// <summary>Flat glTF-frame cloud -> Unity-frame points.</summary>
    public static Vector3[] GltfToUnityPoints(float[] xyz)
    {
        var points = new Vector3[xyz.Length / 3];
        for (var i = 0; i < points.Length; i++)
            points[i] = GltfToUnityPosition(xyz[3 * i], xyz[3 * i + 1], xyz[3 * i + 2]);
        return points;
    }
}
