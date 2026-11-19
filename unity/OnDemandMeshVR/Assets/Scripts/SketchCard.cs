using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// The object's sketch (data/scenes/build_scene.py: the image sent to the edge with format=sketch) shown as a white
/// card with the drawing, standing at the object's place and turned towards the viewer around the vertical axis: the
/// placeholder of an object kept as a 2D image while it has no mesh. The card is the drawing's whole frame at the
/// object's real size, so the drawn object is about as large as the mesh that replaces it.
/// The material is a copy of Resources/StatusPanelBackground (URP Unlit, opaque), whose shader is always in the build.
/// </summary>
public class SketchCard : MonoBehaviour
{
    // MeshPad's sketch camera (models/mesh_wrappers/sketch.py): half the image height at the object's centre, for an
    // object scaled so that its farthest vertex is at 0.5, is tan(yfov / 2) * distance = tan(pi / 9.6) * sqrt(2.5)
    const float HalfFrame = 0.536744f;

    Renderer _renderer;
    Texture2D _texture;
    Material _material;

    public bool Visible
    {
        get => _renderer != null && _renderer.enabled;
        set
        {
            if (_renderer != null)
                _renderer.enabled = value;
        }
    }

    /// <param name="parent">the scene object (its local frame is centred on its bounding box)</param>
    /// <param name="png">the sketch file: black strokes on white</param>
    /// <param name="sketchRadius">scene.json sketch_radius: farthest vertex of the object in its local frame</param>
    /// <returns>null if the image cannot be decoded</returns>
    public static SketchCard Create(Transform parent, byte[] png, float sketchRadius, string objectId)
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = objectId + " sketch" };
        if (!texture.LoadImage(png, true))
        {
            Destroy(texture);
            Debug.LogError($"[SketchCard] {objectId}: the sketch is not a readable image");
            return null;
        }
        texture.filterMode = FilterMode.Trilinear;  // thin 1-pixel strokes: mipmaps keep them steady at a distance
        texture.anisoLevel = 4;
        texture.wrapMode = TextureWrapMode.Clamp;

        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = "Sketch";
        Destroy(go.GetComponent<Collider>());
        go.transform.SetParent(parent, false);
        var side = 2f * HalfFrame * Mathf.Max(2f * sketchRadius, 1f);  // local units: the parent's scale makes metres
        go.transform.localScale = new Vector3(side, side, 1f);

        var card = go.AddComponent<SketchCard>();
        card._texture = texture;
        card._renderer = go.GetComponent<MeshRenderer>();
        card._renderer.shadowCastingMode = ShadowCastingMode.Off;
        card._renderer.receiveShadows = false;
        var template = Resources.Load<Material>("StatusPanelBackground");
        var shader = template != null ? null : Shader.Find("Universal Render Pipeline/Unlit");
        if (template == null && shader == null)
        {
            Debug.LogError("[SketchCard] no URP Unlit material or shader: the sketch cannot be drawn");
            return card;
        }
        card._material = template != null ? new Material(template) : new Material(shader);
        card._material.name = objectId + " sketch";
        if (card._material.HasProperty("_BaseColor"))
            card._material.SetColor("_BaseColor", Color.white);
        if (card._material.HasProperty("_BaseMap"))
            card._material.SetTexture("_BaseMap", texture);
        card._material.mainTexture = texture;
        card._renderer.sharedMaterial = card._material;
        return card;
    }

    void LateUpdate()
    {
        if (!Visible)
            return;
        var cam = Camera.main;
        if (cam == null)
            return;
        // upright, facing the viewer: a quad is seen from the side its +Z points away from
        var away = transform.position - cam.transform.position;
        away.y = 0f;
        if (away.sqrMagnitude > 1e-6f)
            transform.rotation = Quaternion.LookRotation(away, Vector3.up);
    }

    void OnDestroy()
    {
        if (_material != null)
            Destroy(_material);
        if (_texture != null)
            Destroy(_texture);
    }
}
