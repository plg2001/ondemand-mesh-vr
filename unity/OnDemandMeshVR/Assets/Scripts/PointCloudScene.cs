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
/// GameObject per object, placed with its pose and holding the object's sparse cloud.
/// With Reconstruct All On Start every object is sent to the edge server, nearest first, one after the other.
/// This is the step before on-demand: later only the objects in view will be requested.
/// Context menu (three dots of the component, during Play): Reconstruct all objects / Remove all meshes.
/// </summary>
public class PointCloudScene : MonoBehaviour
{
    [Header("Scene")]
    [Tooltip("Folder inside Assets/StreamingAssets that contains scene.json")]
    public string sceneFolder = "Scenes/demo_room";

    [Tooltip("Material of the reconstructed meshes: MeshGrey (Render Face = Both)")]
    public Material meshMaterial;

    [Header("Edge server")]
    public string serverUrl = "http://127.0.0.1:8000";
    public int timeoutSeconds = 600;

    [Header("When / logging")]
    public bool reconstructAllOnStart = true;

    [Tooltip("Append one row per object to edge_timings.csv in Application.persistentDataPath")]
    public bool logToCsv = true;

    readonly List<SceneObject> _objects = new List<SceneObject>();
    SceneManifest _manifest;
    bool _busy;

    public IReadOnlyList<SceneObject> Objects => _objects;

    async void Start()
    {
        if (!LoadScene())
            return;
        if (reconstructAllOnStart)
            await ReconstructAll();
    }

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

        foreach (var old in _objects)
            if (old != null)
                Destroy(old.gameObject);
        _objects.Clear();

        var points = 0;
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
            obj.Init(entry, cloud, meshMaterial);
            _objects.Add(obj);
            points += obj.PointCount;
        }

        var t = _manifest.totals;
        Debug.Log($"[PointCloudScene] '{_manifest.name}': {_objects.Count} objects, {points} points " +
                  $"({points * 12 / 1024f:F1} KB as float32). The original meshes would be {t?.source_triangles} " +
                  $"triangles (~{(t?.source_mesh_bytes ?? 0) / 1024f:F0} KB).");
        if (meshMaterial == null)
            Debug.LogWarning("[PointCloudScene] Mesh Material is empty: assign MeshGrey (Render Face = Both)");
        return _objects.Count > 0;
    }

    [ContextMenu("Reconstruct all objects")]
    void ReconstructAllFromMenu() => _ = ReconstructAll();

    [ContextMenu("Remove all meshes")]
    void RemoveAllMeshes()
    {
        foreach (var o in _objects)
            if (o != null)
                o.RemoveMesh();
    }

    /// <summary>Requests every object, nearest to the camera first, one at a time.</summary>
    public async Task ReconstructAll()
    {
        if (_busy)
        {
            Debug.LogWarning("[PointCloudScene] a reconstruction is already running");
            return;
        }
        _busy = true;
        try
        {
            var client = new EdgeClient(serverUrl, timeoutSeconds);
            var cam = Camera.main;
            var from = cam != null ? cam.transform.position : transform.position;
            var order = _objects.Where(o => o != null)
                                .OrderBy(o => Vector3.Distance(from, o.transform.position)).ToList();
            var timer = Stopwatch.StartNew();
            int done = 0, triangles = 0, vertices = 0, bytesDown = 0;
            foreach (var obj in order)
            {
                if (this == null || obj == null)
                    return;
                var row = await obj.ReconstructAsync(client);
                if (row == null)
                    continue;
                done++;
                triangles += row.Value.Triangles;
                vertices += obj.MeshVertices;
                bytesDown += row.Value.BytesDown;
                Debug.Log("[PointCloudScene] " + EdgeTimingLog.Describe(row.Value));
                if (logToCsv)
                    EdgeTimingLog.Append(row.Value);
            }
            var pointsKb = order.Sum(o => o.PointCount) * 12 / 1024f;
            var meshKb = (vertices * 12 + triangles * 12) / 1024f;  // float32 positions + uint32 indices
            Debug.Log($"[PointCloudScene] reconstructed {done}/{order.Count} objects in {timer.Elapsed.TotalSeconds:F1} s. " +
                      $"Memory: points {pointsKb:F1} KB, reconstructed meshes {triangles} triangles ~{meshKb:F1} KB " +
                      $"({bytesDown / 1024f:F1} KB of glb received)");
        }
        finally
        {
            _busy = false;
        }
    }

    static Vector3 Vector3Of(float[] a, Vector3 fallback) =>
        a != null && a.Length == 3 ? new Vector3(a[0], a[1], a[2]) : fallback;
}
