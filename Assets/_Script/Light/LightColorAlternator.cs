using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Switches the lamp between two preset colors on demand: call SwitchColor()
/// from any script to toggle between colorA and colorB.
/// Only the HDRP light's color is changed; the lamp face takes its look from
/// being lit by it, so no material properties are touched.
/// Attach to any node of the lamp (the Light is auto-found if not assigned).
/// Do not combine with LineLampController on the same lamp - that script owns
/// the light's color.
/// </summary>
public class LightColorAlternator : MonoBehaviour
{
    [SerializeField, Tooltip("First preset color.")]
    private Color colorA = new Color(1f, 0.25f, 0.2f);

    [SerializeField, Tooltip("Second preset color.")]
    private Color colorB = new Color(0.2f, 0.45f, 1f);

    [SerializeField, Tooltip("The HDRP light whose color switches. Auto-filled if empty.")]
    private Light targetLight;

    private HDAdditionalLightData hdData;

    // Whether colorB is the color currently shown (false = colorA).
    private bool showingColorB;

    private bool warnedMissingTarget;

    private void Awake()
    {
        ResolveReferences();
    }

    /// <summary>Toggle the lamp between colorA and colorB.</summary>
    public void SwitchColor()
    {
        if (targetLight == null || hdData == null)
        {
            ResolveReferences();
            if (targetLight == null || hdData == null)
            {
                if (!warnedMissingTarget)
                {
                    warnedMissingTarget = true;
                    Debug.LogWarning($"[LightColorAlternator] No Light found on {name}. " +
                        "Assign it in the Inspector or add it as a child.");
                }
                return;
            }
        }

        ApplyColor(showingColorB ? colorA : colorB);
        showingColorB = !showingColorB;
    }

    private void ApplyColor(Color color)
    {
        hdData.EnableColorTemperature(false);
        hdData.SetColor(color);
        targetLight.color = color;
    }

    private void ResolveReferences()
    {
        if (targetLight == null)
            targetLight = FindInHierarchy<Light>(this);

        if (targetLight != null && hdData == null)
            hdData = targetLight.GetComponent<HDAdditionalLightData>();
    }

    /// <summary>
    /// Find a component on this node or its children; if none, search from the
    /// lamp's topmost parent down. This way the script also works when attached
    /// to the Area Light of a lamp whose other parts are sibling nodes.
    /// </summary>
    private static T FindInHierarchy<T>(Component from) where T : Component
    {
        T found = from.GetComponentInChildren<T>(true);
        if (found != null) return found;

        Transform top = from.transform;
        while (top.parent != null) top = top.parent;
        return top.GetComponentInChildren<T>(true);
    }
}
