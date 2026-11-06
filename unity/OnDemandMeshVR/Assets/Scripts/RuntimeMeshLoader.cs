using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using GLTFast;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Loads a .glb at runtime (instead of dragging it into the scene by hand) and logs how long it took.
/// Phase 1: the file comes from StreamingAssets. Later the same Load() will receive the mesh coming
/// from the edge server, so only the source of the bytes changes.
/// </summary>
public class RuntimeMeshLoader : MonoBehaviour
{
    [Tooltip("File name inside Assets/StreamingAssets")]
    public string fileName = "fastmesh_001_n2048_fastmesh_V1K_cpu.glb";

    [Tooltip("Material applied to every loaded mesh (the generated meshes have no material). Use MeshGrey.")]
    public Material overrideMaterial;

    [Tooltip("Where the mesh appears, relative to the world origin.")]
    public Vector3 position = new Vector3(0f, 1.3f, 2f);

    public bool loadOnStart = true;

    GameObject _current;

    async void Start()
    {
        if (loadOnStart)
            await LoadFromStreamingAssets(fileName);
    }

    public Task<bool> LoadFromStreamingAssets(string name)
    {
        // Application.streamingAssetsPath is a plain folder on PC; on Quest (Android) it is inside the apk,
        // where a "file://" path does not work: that case is handled when we move to the server download.
        var path = Path.Combine(Application.streamingAssetsPath, name);
        return LoadFromUri("file:///" + path.Replace('\\', '/'));
    }

    public async Task<bool> LoadFromUri(string uri)
    {
        var total = Stopwatch.StartNew();

        var gltf = new GltfImport();
        var ok = await gltf.Load(uri);
        var parseMs = total.ElapsedMilliseconds;
        if (!ok)
        {
            Debug.LogError($"[RuntimeMeshLoader] could not load {uri}");
            return false;
        }

        if (_current != null)
            Destroy(_current);

        _current = new GameObject("GeneratedMesh");
        _current.transform.position = position;

        ok = await gltf.InstantiateMainSceneAsync(_current.transform);
        if (!ok)
        {
            Debug.LogError("[RuntimeMeshLoader] instantiate failed");
            return false;
        }

        if (overrideMaterial != null)
            foreach (var r in _current.GetComponentsInChildren<MeshRenderer>())
                r.sharedMaterial = overrideMaterial;

        total.Stop();
        var tris = 0;
        foreach (var f in _current.GetComponentsInChildren<MeshFilter>())
            tris += f.sharedMesh.triangles.Length / 3;

        Debug.Log($"[RuntimeMeshLoader] loaded '{uri}': parse {parseMs} ms, total {total.ElapsedMilliseconds} ms, {tris} triangles");
        return true;
    }
}
