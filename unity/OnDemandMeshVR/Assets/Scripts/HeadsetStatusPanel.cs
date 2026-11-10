using TMPro;
using UnityEngine;

/// <summary>
/// A small text panel that follows the head, below the line of sight: inside the headset there is no Unity
/// Console, so the scene writes here whether the edge server is reachable, what it is doing and the frame rate.
/// </summary>
public class HeadsetStatusPanel : MonoBehaviour
{
    TextMeshPro _text;

    /// <summary>Creates the panel as a child of the camera (head-locked).</summary>
    public static HeadsetStatusPanel Create(Transform head)
    {
        var root = new GameObject("Headset Status Panel");
        root.transform.SetParent(head, false);
        root.transform.localPosition = new Vector3(0f, -0.28f, 1.0f);   // 1 m ahead, below the line of sight
        root.transform.localRotation = Quaternion.Euler(15f, 0f, 0f);    // tilted towards the eyes
        var panel = root.AddComponent<HeadsetStatusPanel>();

        // dark background, so the text is readable against the bright sky
        var background = GameObject.CreatePrimitive(PrimitiveType.Quad);
        background.name = "Background";
        Destroy(background.GetComponent<Collider>());
        background.transform.SetParent(root.transform, false);
        background.transform.localScale = new Vector3(0.66f, 0.2f, 1f);
        var material = Resources.Load<Material>("StatusPanelBackground");
        if (material == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            material = shader != null ? new Material(shader) : null;
            if (material != null && material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", new Color(0.05f, 0.06f, 0.09f));
        }
        if (material != null)
            background.GetComponent<MeshRenderer>().sharedMaterial = material;

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(root.transform, false);
        textGo.transform.localPosition = new Vector3(0f, 0f, -0.005f);  // just in front of the background
        panel._text = textGo.AddComponent<TextMeshPro>();
        panel._text.rectTransform.sizeDelta = new Vector2(0.62f, 0.18f);
        panel._text.fontSize = 0.2f;                                    // 3D TextMeshPro: 1 = 0.1 m em height
        panel._text.alignment = TextAlignmentOptions.TopLeft;
        panel._text.textWrappingMode = TextWrappingModes.Normal;
        panel._text.overflowMode = TextOverflowModes.Truncate;
        panel._text.color = Color.white;
        panel._text.richText = true;
        panel._text.text = "starting...";
        return panel;
    }

    public void SetText(string text)
    {
        if (_text != null)
            _text.text = text;
    }
}
