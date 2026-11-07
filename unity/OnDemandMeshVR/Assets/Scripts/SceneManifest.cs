using System;

// scene.json written by data/scenes/build_scene.py, read with JsonUtility: field names match the JSON keys.
// Poses are in the glTF frame (right-handed, +Y up, metres); convert them with PointCloudIO.

[Serializable]
public class SceneManifest
{
    public int version;
    public string name;
    public SceneObjectEntry[] objects;
    public SceneTotals totals;
}

[Serializable]
public class SceneObjectEntry
{
    public string id;
    public string shape;
    public string points;            // .xyz file name, relative to the scene folder (object's local frame)
    public int point_count;
    public int bytes_xyz_f32;
    public float[] position;         // x, y, z
    public float[] rotation;         // quaternion x, y, z, w
    public float[] scale;            // x, y, z
    public float[] local_bounds_min;
    public float[] local_bounds_max;
    public int source_vertices;      // the original mesh the cloud was sampled from
    public int source_triangles;
    public long source_mesh_bytes;
}

[Serializable]
public class SceneTotals
{
    public int objects;
    public int points;
    public long bytes_xyz_f32;
    public long source_vertices;
    public long source_triangles;
    public long source_mesh_bytes;
}
