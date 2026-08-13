using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Switches the lamp between two preset colors on demand: call SwitchColor()
/// from any script to toggle between colorA and colorB.
/// Every HDRP light on the lamp switches together. Only the lights' color is
/// changed; the lamp face takes its look from being lit by them, so no
/// material properties are touched.
/// Attach to the lamp root for the automatic search to find all its lights.
/// Assign the lights in the Inspector to override the automatic search.
/// Do not combine with LineLampController on the same lamp - that script owns
/// the lights' color.
/// </summary>
public class LightColorAlternator : MonoBehaviour
{
    [SerializeField, Tooltip("First preset color.")]
    private Color colorA = new Color(1f, 0.25f, 0.2f);

    [SerializeField, Tooltip("Second preset color.")]
    private Color colorB = new Color(0.2f, 0.45f, 1f);

    [SerializeField, Tooltip("The HDRP lights whose colors switch. Auto-filled with every Light under this node if empty.")]
    private Light[] targetLights;

    // Resolved targets: parallel arrays rebuilt in ResolveReferences.
    private Light[] resolvedLights;
    private HDAdditionalLightData[] resolvedHdDatas;

    // Whether colorB is the color currently shown (false = colorA).
    private bool showingColorB;

    private bool warnedMissingTarget;

    private void Awake()
    {
        ResolveReferences();
    }

    /// <summary>Toggle every light on the lamp between colorA and colorB.</summary>
    public void SwitchColor()
    {
        if (!HasResolvedLights())
        {
            ResolveReferences();
            if (!HasResolvedLights())
            {
                if (!warnedMissingTarget)
                {
                    warnedMissingTarget = true;
                    Debug.LogWarning($"[LightColorAlternator] No Light found on {name}. " +
                        "Assign them in the Inspector or add them as children.");
                }
                return;
            }
        }

        ApplyColor(showingColorB ? colorA : colorB);
        showingColorB = !showingColorB;
    }

    private bool HasResolvedLights()
    {
        return resolvedLights != null && resolvedLights.Length > 0;
    }

    private void ApplyColor(Color color)
    {
        for (int i = 0; i < resolvedLights.Length; i++)
        {
            HDAdditionalLightData hd = resolvedHdDatas[i];
            if (hd == null) continue;

            hd.EnableColorTemperature(false);
            hd.SetColor(color);
            resolvedLights[i].color = color;
        }
    }

    private void ResolveReferences()
    {
        // The Inspector list wins; when empty, take every light under this
        // node, then under the parent (covers being attached to the Plane),
        // and finally under the topmost parent.
        if (targetLights == null || targetLights.Length == 0)
        {
            targetLights = GetComponentsInChildren<Light>(true);
            if (targetLights.Length == 0 && transform.parent != null)
            {
                targetLights = transform.parent.GetComponentsInChildren<Light>(true);
                if (targetLights.Length == 0)
                {
                    Transform top = transform;
                    while (top.parent != null) top = top.parent;
                    targetLights = top.GetComponentsInChildren<Light>(true);
                }
            }
        }

        // Build the matching HDRP data array, dropping null entries.
        int count = 0;
        foreach (var l in targetLights)
        {
            if (l != null) count++;
        }

        resolvedLights = new Light[count];
        resolvedHdDatas = new HDAdditionalLightData[count];
        int index = 0;
        foreach (var l in targetLights)
        {
            if (l == null) continue;
            resolvedLights[index] = l;
            resolvedHdDatas[index] = l.GetComponent<HDAdditionalLightData>();
            index++;
        }
    }
}
