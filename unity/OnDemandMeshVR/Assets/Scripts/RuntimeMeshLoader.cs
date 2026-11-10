using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using GLTFast;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// Loads a .glb at runtime (instead of dragging it into the scene by hand) and logs how long it took.
/// Two sources: a file in StreamingAssets (local test) or a byte array already in memory, which is how the
/// mesh arrives from the edge server (see EdgeMeshClient and SceneObject).
/// </summary>
public class RuntimeMeshLoader : MonoBehaviour
{
    [Tooltip("File name inside Assets/StreamingAssets, used when Load On Start is ticked")]
    public string fileName = "fastmesh_001_n2048_fastmesh_V1K_cpu.glb";

    [Tooltip("Material applied to every loaded mesh (the generated meshes have no material). Use MeshGrey, " +
             "with Render Face = Both: the generated faces are not consistently oriented.")]
    public Material overrideMaterial;

    [Tooltip("Where the mesh appears, in world coordinates (ignored when Attach To This Object is ticked).")]
    public Vector3 position = new Vector3(0f, 1.3f, 2f);

    [Tooltip("The mesh becomes a child of this GameObject and follows its position, rotation and scale.")]
    public bool attachToThisObject;

    public bool loadOnStart = true;

    /// <summary>Milliseconds of the last successful load: glb parsing + GameObject creation + material.</summary>
    public double LastLoadMs { get; private set; }

    /// <summary>Triangles of the mesh currently shown (0 if none).</summary>
    public int LastTriangles { get; private set; }

    /// <summary>Vertices of the mesh currently shown (0 if none).</summary>
    public int LastVertices { get; private set; }

    GameObject _current;
    GltfImport _currentImport;  // owns the meshes of _current: disposing it frees them

    async void Start()
    {
        if (loadOnStart)
            await LoadFromStreamingAssets(fileName);
    }

    void OnDestroy() => _currentImport?.Dispose();

    public Task<bool> LoadFromStreamingAssets(string name)
    {
        // Application.streamingAssetsPath is a plain folder on PC (needs "file:///"); on Quest (Android) it is
        // already a "jar:file://" URL inside the apk, which glTFast reads through UnityWebRequest.
        var path = Path.Combine(Application.streamingAssetsPath, name).Replace('\\', '/');
        return LoadFromUri(path.Contains("://") ? path : "file:///" + path);
    }

    public async Task<bool> LoadFromUri(string uri)
    {
        var timer = Stopwatch.StartNew();
        var gltf = new GltfImport();
        if (!await gltf.Load(uri))
        {
            Debug.LogError($"[RuntimeMeshLoader] could not load {uri}");
            gltf.Dispose();
            return false;
        }
        return await Show(gltf, uri, timer);
    }

    /// <summary>Loads a .glb that is already in memory, e.g. the body of the edge server's answer.</summary>
    public async Task<bool> LoadFromBytes(byte[] glb, string label)
    {
        var timer = Stopwatch.StartNew();
        var gltf = new GltfImport();
        if (!await gltf.Load(glb))
        {
            Debug.LogError($"[RuntimeMeshLoader] invalid glb for '{label}' ({glb.Length} bytes)");
            gltf.Dispose();
            return false;
        }
        return await Show(gltf, label, timer);
    }

    /// <summary>Removes the mesh currently shown and frees its memory.</summary>
    public void Clear()
    {
        if (_current != null)
            Destroy(_current);
        _currentImport?.Dispose();
        _current = null;
        _currentImport = null;
        LastTriangles = LastVertices = 0;
    }

    async Task<bool> Show(GltfImport gltf, string label, Stopwatch timer)
    {
        var parseMs = timer.Elapsed.TotalMilliseconds;
        if (this == null)  // Play mode stopped while we were loading
        {
            gltf.Dispose();
            return false;
        }
        var root = new GameObject("GeneratedMesh");
        if (attachToThisObject)
            root.transform.SetParent(transform, false);
        else
            root.transform.position = position;
        if (!await gltf.InstantiateMainSceneAsync(root.transform))
        {
            Debug.LogError($"[RuntimeMeshLoader] instantiate failed for '{label}'");
            Destroy(root);
            gltf.Dispose();
            return false;
        }
        if (this == null)
        {
            gltf.Dispose();
            return false;
        }

        if (overrideMaterial != null)
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
                r.sharedMaterial = overrideMaterial;

        // replace the previous mesh only now, so it stays visible while the new one is loading
        Clear();
        _current = root;
        _currentImport = gltf;

        timer.Stop();
        LastLoadMs = timer.Elapsed.TotalMilliseconds;
        (LastTriangles, LastVertices) = CountGeometry(root);
        Debug.Log($"[RuntimeMeshLoader] loaded '{label}': parse {parseMs:F0} ms, total {LastLoadMs:F0} ms, " +
                  $"{LastTriangles} triangles");
        return true;
    }

    static (int triangles, int vertices) CountGeometry(GameObject root)
    {
        long indices = 0, vertices = 0;
        foreach (var f in root.GetComponentsInChildren<MeshFilter>())
        {
            var mesh = f.sharedMesh;
            if (mesh == null) continue;
            vertices += mesh.vertexCount;
            for (var i = 0; i < mesh.subMeshCount; i++)
                indices += mesh.GetIndexCount(i);
        }
        return ((int)(indices / 3), (int)vertices);
    }
}
