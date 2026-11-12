using System.Diagnostics;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// One object of a point-cloud scene: it owns the object's sparse cloud (local frame), draws it as points while
/// there is no mesh, and shows the mesh once the edge has reconstructed it. Created at runtime by
/// PointCloudScene. In the Scene view (and in the Game view with Gizmos on) its bounding box tells the state:
/// grey = points, out of view; cyan = in view, waiting; yellow = request running; green = mesh; red = failed,
/// will be retried; magenta = failed, not retried (the server gave up on it or refused it).
/// </summary>
[RequireComponent(typeof(RuntimeMeshLoader))]
public class SceneObject : MonoBehaviour
{
    public enum State { PointsOnly, Requesting, MeshReady, Failed }

    /// <summary>Why the last request failed: decides whether, and when, the object is asked for again.</summary>
    public enum Failure
    {
        None,
        Connection,  // the server could not be reached: requests pause until /health answers again
        Transient,   // a server error that may not happen again: retried, with a pause that grows
        TooSlow,     // the server gave up (time budget, HTTP 504): the same cloud would fail again
        Refused      // the server will not serve it (HTTP 4xx, e.g. 409 with --objects), or its mesh is unreadable
    }

    [Tooltip("Read-only: filled from scene.json")]
    public string objectId;
    public string shape;
    public State state = State.PointsOnly;

    [Tooltip("Read-only: inside the camera view (on-demand mode)")]
    public bool inView;

    /// <summary>What is known about the mesh shown: written on the label above the object.</summary>
    public struct MeshInfo
    {
        public string Model;
        public int Triangles;
        public double Seconds;                                // from the request to the mesh shown
        public double FScore, Precision, Recall, ChamferL2;   // vs the original mesh; NaN = not measured
    }

    float[] _pointsGltf;          // exactly what is stored and sent: glTF frame, object's local frame
    RuntimeMeshLoader _loader;
    PointCloudRenderer _points;
    ObjectLabel _label;           // null when labels are off
    byte[] _cachedGlb;            // last mesh received, kept only if the scene caches meshes

    public MeshInfo Info { get; private set; }

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

    /// <summary>Error of the last failed request ("" if none), its kind and how many requests failed in a row.</summary>
    public string LastError { get; private set; } = "";
    public Failure LastFailure { get; private set; }
    public int FailedAttempts { get; private set; }

    /// <summary>Asking again can help: the server was unreachable or had a passing error.</summary>
    public bool WillRetry => LastFailure is Failure.Connection or Failure.Transient;

    /// <summary>The server serves only some objects (--objects, HTTP 409): expected, not an error.</summary>
    public bool NotServedByChoice { get; private set; }

    public void Init(SceneObjectEntry entry, float[] pointsGltf, Material meshMaterial, Material pointMaterial,
                     float pointSize, Color pointColor, bool showLabel)
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

        _loader = GetComponent<RuntimeMeshLoader>();  // the mesh comes back in the object's local frame
        _loader.overrideMaterial = meshMaterial;

        var pointsGo = new GameObject("Points");
        pointsGo.transform.SetParent(transform, false);
        _points = pointsGo.AddComponent<PointCloudRenderer>();
        _points.Show(LocalPoints, pointMaterial, pointSize, pointColor);
        if (showLabel)
            _label = ObjectLabel.Create(transform, WorldBounds);
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
            SetFailed(Classify(response), response.Error);
            NotServedByChoice = response.StatusCode == 409;
            if (NotServedByChoice)
                Debug.Log($"[SceneObject] {objectId}: not served by this server, stays as points ({response.Error})");
            else
                Debug.LogError($"[SceneObject] {objectId}: {response.Error} ({LastFailure}" +
                               $"{(WillRetry ? "" : ", not retried")})");
            return null;
        }
        if (!await _loader.LoadFromBytes(response.Glb, objectId) || this == null)
        {
            if (this != null)
                SetFailed(Failure.Refused, "the mesh received could not be loaded");
            return null;
        }
        LastError = "";
        LastFailure = Failure.None;
        FailedAttempts = 0;
        NotServedByChoice = false;
        total.Stop();
        Info = new MeshInfo
        {
            Model = response.ModelLabel, Triangles = _loader.LastTriangles, Seconds = total.Elapsed.TotalSeconds,
            FScore = response.FScore, Precision = response.Precision, Recall = response.Recall,
            ChamferL2 = response.ChamferL2
        };
        ShowMesh(false);
        if (keepGlb)
            _cachedGlb = response.Glb;
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
            if (this != null)
                SetFailed(Failure.Refused, "the cached mesh could not be loaded");
            return false;
        }
        ShowMesh(true);
        return true;
    }

    /// <summary>Forgets the failures: the object goes back to points and will be asked for again.</summary>
    public void ResetFailure()
    {
        if (state == State.Failed)
            state = State.PointsOnly;
        LastError = "";
        LastFailure = Failure.None;
        FailedAttempts = 0;
        NotServedByChoice = false;
    }

    void SetFailed(Failure kind, string error)
    {
        state = State.Failed;
        LastFailure = kind;
        LastError = error;
        NotServedByChoice = false;  // set again by the caller when it is a 409
        FailedAttempts++;
        FailedAt = Time.realtimeSinceStartupAsDouble;
    }

    static Failure Classify(EdgeClient.Response r)
    {
        if (r.ConnectionFailed)
            return Failure.Connection;
        if (r.StatusCode == 504)
            return Failure.TooSlow;
        if (r.StatusCode >= 400 && r.StatusCode < 500 && r.StatusCode != 408 && r.StatusCode != 429)
            return Failure.Refused;
        return Failure.Transient;  // other 5xx, 408 / 429, or no status at all
    }

    /// <summary>Back to points: the mesh is destroyed and its memory freed (the glb cache, if any, is kept).</summary>
    public void RemoveMesh()
    {
        _loader.Clear();
        state = State.PointsOnly;
        if (_points != null)
            _points.Visible = true;
        if (_label != null)
            _label.Hide();
    }

    void ShowMesh(bool fromCache)
    {
        state = State.MeshReady;
        if (_points != null)
            _points.Visible = false;
        if (_label != null)
            _label.Show(LabelText(fromCache));
    }

    string LabelText(bool fromCache)
    {
        var i = Info;
        var text = $"<b>{objectId}</b>  {i.Model}\n{i.Triangles} triangles, {i.Seconds:F1} s{(fromCache ? " (cache)" : "")}\n";
        return double.IsNaN(i.FScore)
            ? text + "<color=#a0a0a0>accuracy not measured</color>"
            : text + $"F-score <b>{i.FScore:F2}</b>  Chamfer {i.ChamferL2:F4}\n" +
                     $"precision {i.Precision:F2}  recall {i.Recall:F2}";
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
            State.Failed => WillRetry ? Color.red : Color.magenta,
            _ => inView ? Color.cyan : new Color(0.75f, 0.75f, 0.75f)
        };
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(LocalBounds.center, LocalBounds.size);
    }
}
