using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// One object of a point-cloud scene: it owns the object's sparse cloud (local frame), draws it as points while
/// there is no mesh, and shows the mesh once the edge has reconstructed it. Created at runtime by
/// PointCloudScene. In the Scene view (and in the Game view with Gizmos on) its bounding box tells the state:
/// grey = points, out of view; cyan = in view, waiting; yellow = request running; green = mesh; red = failed.
/// </summary>
[RequireComponent(typeof(RuntimeMeshLoader))]
public class SceneObject : MonoBehaviour
{
    public enum State { PointsOnly, Requesting, MeshReady, Failed }

    [Tooltip("Read-only: filled from scene.json")]
    public string objectId;
    public string shape;
    public State state = State.PointsOnly;

    [Tooltip("Read-only: inside the camera view (on-demand mode)")]
    public bool inView;

    float[] _pointsGltf;          // exactly what is stored and sent: glTF frame, object's local frame
    RuntimeMeshLoader _loader;
    PointCloudRenderer _points;
    byte[] _cachedGlb;            // last mesh received, kept only if the scene caches meshes

    /// <summary>The cloud in Unity's frame, local to this object.</summary>
    public Vector3[] LocalPoints { get; private set; }
    public Bounds LocalBounds { get; private set; }
    public Bounds WorldBounds { get; private set; }  // scene objects do not move: computed once
    public int PointCount => LocalPoints?.Length ?? 0;
    public int MeshTriangles => state == State.MeshReady ? _loader.LastTriangles : 0;
    public int MeshVertices => state == State.MeshReady ? _loader.LastVertices : 0;
    public bool HasCachedMesh => _cachedGlb != null;
    public int CachedBytes => _cachedGlb?.Length ?? 0;
    public double LastLoadMs => _loader != null ? _loader.LastLoadMs : 0;

    // timestamps (Time.realtimeSinceStartupAsDouble) used by the on-demand logic
    public double VisibleSince { get; set; } = -1;
    public double LastSeen { get; set; } = -1;
    public double FailedAt { get; set; } = -1;

    /// <summary>Error of the last failed request ("" if none) and whether the server was unreachable.</summary>
    public string LastError { get; private set; } = "";
    public bool LastFailureWasConnection { get; private set; }

    public void Init(SceneObjectEntry entry, float[] pointsGltf, Material meshMaterial, Material pointMaterial,
                     float pointSize, Color pointColor)
    {
        objectId = entry.id;
        shape = entry.shape;
        _pointsGltf = pointsGltf;
        LocalPoints = PointCloudIO.GltfToUnityPoints(pointsGltf);
        var bounds = new Bounds(LocalPoints[0], Vector3.zero);
        foreach (var p in LocalPoints)
            bounds.Encapsulate(p);
        LocalBounds = bounds;
        WorldBounds = ToWorld(transform.localToWorldMatrix, bounds);

        _loader = GetComponent<RuntimeMeshLoader>();
        _loader.loadOnStart = false;
        _loader.attachToThisObject = true;  // the mesh comes back in the object's local frame
        _loader.overrideMaterial = meshMaterial;

        var pointsGo = new GameObject("Points");
        pointsGo.transform.SetParent(transform, false);
        _points = pointsGo.AddComponent<PointCloudRenderer>();
        _points.Show(LocalPoints, pointMaterial, pointSize, pointColor);
    }

    /// <summary>Sends the cloud to the edge and shows the mesh that comes back. Returns the timings, or null.</summary>
    public async Task<EdgeTimingLog.Row?> ReconstructAsync(EdgeClient client, string source, bool keepGlb)
    {
        state = State.Requesting;
        var total = Stopwatch.StartNew();
        var body = PointCloudIO.ToBytes(_pointsGltf);
        var prepareMs = total.Elapsed.TotalMilliseconds;
        var response = await client.ReconstructAsync(body, objectId);
        if (this == null)  // Play mode stopped while waiting
            return null;
        if (!response.Ok)
        {
            state = State.Failed;
            LastError = response.Error;
            LastFailureWasConnection = response.ConnectionFailed;
            Debug.LogError($"[SceneObject] {objectId}: {response.Error}");
            return null;
        }
        LastError = "";
        LastFailureWasConnection = false;
        if (!await _loader.LoadFromBytes(response.Glb, objectId) || this == null)
        {
            state = State.Failed;
            return null;
        }
        ShowMesh();
        if (keepGlb)
            _cachedGlb = response.Glb;
        total.Stop();
        return EdgeTimingLog.From(objectId, source, PointCount, response, _loader.LastTriangles, _loader.LastLoadMs,
                                  prepareMs, total.Elapsed.TotalMilliseconds);
    }

    /// <summary>Shows the mesh received earlier again, without asking the edge.</summary>
    public async Task<bool> ShowCachedMeshAsync()
    {
        if (_cachedGlb == null)
            return false;
        state = State.Requesting;
        if (!await _loader.LoadFromBytes(_cachedGlb, objectId + " (cache)") || this == null)
        {
            state = State.Failed;
            return false;
        }
        ShowMesh();
        return true;
    }

    /// <summary>Back to points: the mesh is destroyed and its memory freed (the glb cache, if any, is kept).</summary>
    public void RemoveMesh()
    {
        _loader.Clear();
        state = State.PointsOnly;
        if (_points != null)
            _points.Visible = true;
    }

    void ShowMesh()
    {
        state = State.MeshReady;
        if (_points != null)
            _points.Visible = false;
    }

    static Bounds ToWorld(Matrix4x4 m, Bounds local)
    {
        var c = local.center;
        var e = local.extents;
        var world = new Bounds(m.MultiplyPoint3x4(c), Vector3.zero);
        for (var i = 0; i < 8; i++)
        {
            var corner = c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
            world.Encapsulate(m.MultiplyPoint3x4(corner));
        }
        return world;
    }

    void OnDrawGizmos()
    {
        if (LocalPoints == null)
            return;
        Gizmos.color = state switch
        {
            State.Requesting => Color.yellow,
            State.MeshReady => Color.green,
            State.Failed => Color.red,
            _ => inView ? Color.cyan : new Color(0.75f, 0.75f, 0.75f)
        };
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(LocalBounds.center, LocalBounds.size);
    }
}
