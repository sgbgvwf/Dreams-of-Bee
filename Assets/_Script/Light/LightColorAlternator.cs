using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using System.Collections.Generic;

/// <summary>
/// Switches the lamp between two preset colors on demand: call SwitchColor()
/// from any script to toggle between colorA and colorB.
/// Every HDRP light on the lamp switches together, and the lamp's emissive
/// faces follow along: their material _Color is set to the same color.
/// The face materials are instanced per renderer at runtime, so other
/// objects sharing the same material asset are unaffected.
/// Attach to the lamp root for the automatic search to find all its lights
/// and emissive faces. Assign the lights/renderers in the Inspector to
/// override the automatic search.
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

    [SerializeField, Tooltip("The lamp's emissive faces whose _Color follows the preset. Auto-filled with every lit renderer under this node if empty.")]
    private Renderer[] emissiveRenderers;

    // Resolved targets: parallel arrays rebuilt in ResolveReferences.
    private Light[] resolvedLights;
    private HDAdditionalLightData[] resolvedHdDatas;
    private Material[] faceMaterials;  // runtime instances of the faces' materials

    // Whether colorB is the color currently shown (false = colorA).
    private bool showingColorB;

    private bool warnedMissingTarget;

    private static readonly int ColorPropertyId = Shader.PropertyToID("_Color");
    private static readonly int LightPropertyId = Shader.PropertyToID("_Light");

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

    /// <summary>
    /// Show a specific preset color (true = colorB, false = colorA);
    /// no-op when that color is already showing, so repeated calls never
    /// flip the lamp back and forth.
    /// </summary>
    public void ShowColor(bool useColorB)
    {
        if (useColorB == showingColorB) return;
        SwitchColor();
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

        for (int i = 0; i < faceMaterials.Length; i++)
        {
            if (faceMaterials[i] != null)
                faceMaterials[i].SetColor(ColorPropertyId, color);
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

        // Emissive faces: prefer the Inspector list; when empty, take every
        // renderer whose material is lit (_Light > 0) under this node, then
        // under the parent, and finally under the topmost parent.
        if (emissiveRenderers == null || emissiveRenderers.Length == 0)
        {
            Renderer[] found = transform.GetComponentsInChildren<Renderer>(true);
            if (found.Length == 0 && transform.parent != null)
            {
                Transform top = transform;
                while (top.parent != null) top = top.parent;
                found = top.GetComponentsInChildren<Renderer>(true);
            }

            List<Renderer> lit = new List<Renderer>();
            foreach (var r in found)
            {
                if (r == null) continue;
                Material m = r.sharedMaterial;
                if (m != null && m.HasProperty(LightPropertyId) && m.GetFloat(LightPropertyId) > 0f)
                    lit.Add(r);
            }
            emissiveRenderers = lit.ToArray();
        }

        // Runtime instances: changes affect only these renderers, not other
        // objects sharing the same material asset.
        faceMaterials = new Material[emissiveRenderers.Length];
        for (int i = 0; i < emissiveRenderers.Length; i++)
        {
            if (emissiveRenderers[i] != null)
                faceMaterials[i] = emissiveRenderers[i].material;
        }
    }
}
