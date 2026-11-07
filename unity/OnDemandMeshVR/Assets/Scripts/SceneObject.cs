using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// One object of a point-cloud scene: it owns the object's sparse cloud (local frame) and, once reconstructed by
/// the edge, its mesh. Created at runtime by PointCloudScene. In the Scene view (and in the Game view with
/// Gizmos on) its bounding box is drawn in a colour that tells the state.
/// </summary>
[RequireComponent(typeof(RuntimeMeshLoader))]
public class SceneObject : MonoBehaviour
{
    public enum State { PointsOnly, Requesting, MeshReady, Failed }

    [Tooltip("Read-only: filled from scene.json")]
    public string objectId;
    public string shape;
    public State state = State.PointsOnly;

    float[] _pointsGltf;          // exactly what is stored and sent: glTF frame, object's local frame
    RuntimeMeshLoader _loader;

    /// <summary>The cloud in Unity's frame, local to this object (for drawing it later).</summary>
    public Vector3[] LocalPoints { get; private set; }
    public Bounds LocalBounds { get; private set; }
    public int PointCount => LocalPoints?.Length ?? 0;
    public int MeshTriangles => state == State.MeshReady ? _loader.LastTriangles : 0;
    public int MeshVertices => state == State.MeshReady ? _loader.LastVertices : 0;

    public void Init(SceneObjectEntry entry, float[] pointsGltf, Material meshMaterial)
    {
        objectId = entry.id;
        shape = entry.shape;
        _pointsGltf = pointsGltf;
        LocalPoints = PointCloudIO.GltfToUnityPoints(pointsGltf);
        var bounds = new Bounds(LocalPoints[0], Vector3.zero);
        foreach (var p in LocalPoints)
            bounds.Encapsulate(p);
        LocalBounds = bounds;

        _loader = GetComponent<RuntimeMeshLoader>();
        _loader.loadOnStart = false;
        _loader.attachToThisObject = true;  // the mesh comes back in the object's local frame
        _loader.overrideMaterial = meshMaterial;
    }

    /// <summary>Sends the cloud to the edge and shows the mesh that comes back. Returns the timings, or null.</summary>
    public async Task<EdgeTimingLog.Row?> ReconstructAsync(EdgeClient client)
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
            Debug.LogError($"[SceneObject] {objectId}: {response.Error}");
            return null;
        }
        if (!await _loader.LoadFromBytes(response.Glb, objectId))
        {
            state = State.Failed;
            return null;
        }
        state = State.MeshReady;
        total.Stop();
        return EdgeTimingLog.From(objectId, "scene", PointCount, response, _loader.LastTriangles, _loader.LastLoadMs,
                                  prepareMs, total.Elapsed.TotalMilliseconds);
    }

    /// <summary>Back to "points only": the mesh is destroyed and its memory freed.</summary>
    public void RemoveMesh()
    {
        _loader.Clear();
        state = State.PointsOnly;
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
            _ => new Color(0.75f, 0.75f, 0.75f)
        };
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(LocalBounds.center, LocalBounds.size);
    }
}
