using System;

/// <summary>
/// A tiny .glb (a tetrahedron: 4 vertices, 4 triangles, 788 bytes) written by the edge server's own exporter, so it
/// has the same layout as every mesh the server sends: POSITION only, no material, 32-bit indices. PointCloudScene
/// loads and draws it once at start (warm-up), so that the first real mesh does not pay glTFast's first load and the
/// GPU's preparation of the mesh shader. To regenerate it, from the repository root:
///   python -c "import base64,trimesh; from edge.reconstruction import export_glb; print(base64.b64encode(export_glb(
///   trimesh.Trimesh([[0,0,0],[1,0,0],[0,1,0],[0,0,1]],[[0,2,1],[0,1,3],[0,3,2],[1,2,3]]))).decode())"
/// </summary>
public static class WarmUpMesh
{
    public static readonly byte[] Glb = Convert.FromBase64String(
        "Z2xURgIAAAAUAwAAmAIAAEpTT057InNjZW5lIjowLCJzY2VuZXMiOlt7Im5vZGVzIjpbMF19XSwiYXNzZXQiOnsidmVyc2lv" +
        "biI6IjIuMCIsImdlbmVyYXRvciI6Imh0dHBzOi8vZ2l0aHViLmNvbS9taWtlZGgvdHJpbWVzaCJ9LCJhY2Nlc3NvcnMiOlt7" +
        "ImNvbXBvbmVudFR5cGUiOjUxMjUsInR5cGUiOiJTQ0FMQVIiLCJidWZmZXJWaWV3IjowLCJjb3VudCI6MTIsIm1heCI6WzNd" +
        "LCJtaW4iOlswXX0seyJjb21wb25lbnRUeXBlIjo1MTI2LCJ0eXBlIjoiVkVDMyIsImJ5dGVPZmZzZXQiOjAsImJ1ZmZlclZp" +
        "ZXciOjEsImNvdW50Ijo0LCJtYXgiOlsxLjAsMS4wLDEuMF0sIm1pbiI6WzAuMCwwLjAsMC4wXX1dLCJtZXNoZXMiOlt7Im5h" +
        "bWUiOiJnZW9tZXRyeV8wIiwiZXh0cmFzIjp7InByb2Nlc3NlZCI6dHJ1ZX0sInByaW1pdGl2ZXMiOlt7ImF0dHJpYnV0ZXMi" +
        "OnsiUE9TSVRJT04iOjF9LCJpbmRpY2VzIjowLCJtb2RlIjo0fV19XSwibm9kZXMiOlt7Im5hbWUiOiJ3b3JsZCIsImNoaWxk" +
        "cmVuIjpbMV19LHsibmFtZSI6Imdlb21ldHJ5XzAiLCJtZXNoIjowfV0sImJ1ZmZlcnMiOlt7ImJ5dGVMZW5ndGgiOjk2fV0s" +
        "ImJ1ZmZlclZpZXdzIjpbeyJidWZmZXIiOjAsImJ5dGVPZmZzZXQiOjAsImJ5dGVMZW5ndGgiOjQ4fSx7ImJ1ZmZlciI6MCwi" +
        "Ynl0ZU9mZnNldCI6NDgsImJ5dGVMZW5ndGgiOjQ4fV19ICAgYAAAAEJJTgAAAAAAAgAAAAEAAAAAAAAAAQAAAAMAAAAAAAAA" +
        "AwAAAAIAAAABAAAAAgAAAAMAAAAAAAAAAAAAAAAAAAAAAIA/AAAAAAAAAAAAAAAAAACAPwAAAAAAAAAAAAAAAAAAgD8=");
}
