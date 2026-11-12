using TMPro;
using UnityEngine;

/// <summary>
/// A small text panel floating above a scene object (created by SceneObject while its mesh is shown): which model made
/// the mesh, its triangles and time, and its accuracy against the original mesh. It always turns towards the camera
/// (around the vertical axis) and grows with the distance, so it reads the same near and far.
/// </summary>
public class ObjectLabel : MonoBehaviour
{
    const float ReferenceDistance = 1.5f;  // at this distance the label has its base size (0.40 x 0.13 m)
    const float BaseHeight = 0.13f;

    TextMeshPro _text;
    Vector3 _anchor;  // world position of the top centre of the object

    /// <summary>Creates a hidden label above the world bounds of an object, as a child of it.</summary>
    public static ObjectLabel Create(Transform owner, Bounds worldBounds)
    {
        var root = new GameObject("Label");
        root.transform.SetParent(owner, false);
        var label = root.AddComponent<ObjectLabel>();
        label._anchor = new Vector3(worldBounds.center.x, worldBounds.max.y, worldBounds.center.z);

        var background = GameObject.CreatePrimitive(PrimitiveType.Quad);
        background.name = "Background";
        Destroy(background.GetComponent<Collider>());
        background.transform.SetParent(root.transform, false);
        background.transform.localScale = new Vector3(0.40f, BaseHeight, 1f);
        var material = Resources.Load<Material>("StatusPanelBackground");  // the status panel's: always in the build
        if (material != null)
            background.GetComponent<MeshRenderer>().sharedMaterial = material;

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(root.transform, false);
        textGo.transform.localPosition = new Vector3(0f, 0f, -0.003f);  // just in front of the background
        label._text = textGo.AddComponent<TextMeshPro>();
        label._text.rectTransform.sizeDelta = new Vector2(0.37f, BaseHeight - 0.01f);
        label._text.fontSize = 0.15f;  // 3D TextMeshPro: 1 = 0.1 m em height
        label._text.alignment = TextAlignmentOptions.Center;
        label._text.textWrappingMode = TextWrappingModes.Normal;
        label._text.overflowMode = TextOverflowModes.Truncate;
        label._text.color = Color.white;
        label._text.richText = true;
        root.SetActive(false);
        return label;
    }

    public void Show(string text)
    {
        _text.text = text;
        gameObject.SetActive(true);
        LateUpdate();  // placed at once, not one frame late
    }

    public void Hide() => gameObject.SetActive(false);

    void LateUpdate()
    {
        var cam = Camera.main;
        if (cam == null)
            return;
        var toCamera = cam.transform.position - _anchor;
        var size = Mathf.Clamp(toCamera.magnitude / ReferenceDistance, 0.6f, 4f);
        var parentScale = transform.parent != null ? transform.parent.lossyScale.x : 1f;  // objects scale uniformly
        transform.localScale = Vector3.one * (size / Mathf.Max(parentScale, 1e-4f));
        transform.position = _anchor + Vector3.up * (0.04f + 0.5f * BaseHeight * size);
        var flat = new Vector3(toCamera.x, 0f, toCamera.z);
        if (flat.sqrMagnitude > 1e-6f)
            transform.rotation = Quaternion.LookRotation(-flat, Vector3.up);  // text and quad face -Z: the camera
    }
}
