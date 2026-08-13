using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Simulates a light with a loose connection: the HDRP light's intensity
/// wanders randomly between minBrightness and maxBrightness, occasionally
/// snapping off for a brief blackout, then recovering - like a badly-wired
/// lamp.
/// The lamp face's emission follows the light: the face material's _Light
/// float (the emission multiplier of the project's Light shader) is scaled
/// by the same brightness value, so the face dims together with the light.
/// Material colors are never touched.
/// Baselines are captured at Start, so the Inspector light/material setup
/// still defines the look. The face material is instanced per renderer at
/// runtime, so other objects sharing the same material asset are unaffected.
/// Attach to any node of the lamp. Assign the visual Plane in the Inspector;
/// if left empty it auto-fills with the child named "Plane".
/// Play Mode only. Do not combine with LineLampController on the same lamp -
/// that script owns the light's color/intensity.
/// </summary>
public class FlickeringLight : MonoBehaviour
{
    [SerializeField, Tooltip("Lowest brightness as a fraction of the Start-time intensity (0..1).")]
    [Range(0f, 1f)]
    private float minBrightness = 0.15f;

    [SerializeField, Tooltip("Highest brightness as a fraction of the Start-time intensity (0..1).")]
    [Range(0f, 1f)]
    private float maxBrightness = 1f;

    [SerializeField, Tooltip("How fast the brightness moves toward each new target (fraction per second). Higher = sharper flicker.")]
    [Min(0.01f)]
    private float flickerSpeed = 8f;

    [SerializeField, Tooltip("Shortest time between two brightness changes.")]
    [Min(0.01f)]
    private float minChangeInterval = 0.05f;

    [SerializeField, Tooltip("Longest time between two brightness changes.")]
    [Min(0.01f)]
    private float maxChangeInterval = 0.35f;

    [SerializeField, Tooltip("Chance (0..1) that a change is a sudden full blackout.")]
    [Range(0f, 1f)]
    private float blackoutChance = 0.15f;

    [SerializeField, Tooltip("Seconds a blackout lasts before the light comes back.")]
    [Min(0.01f)]
    private float blackoutDuration = 0.1f;

    [SerializeField, Tooltip("The HDRP light whose intensity flickers. Auto-filled if empty.")]
    private Light targetLight;

    [SerializeField, Tooltip("The lamp's visual Plane whose emission follows the light. Auto-filled with the child named 'Plane' if empty. Optional.")]
    private Transform plane;

    private HDAdditionalLightData hdData;
    private Material faceMaterial;  // runtime instance of the plane's material

    // --- Baselines captured at Start ---
    private float baseIntensity;  // the light's intensity
    private float baseEmission;   // the face material's _Light float
    private bool hasFaceEmission;

    // --- Flicker state ---
    private float currentBrightness = 1f;
    private float targetBrightness = 1f;
    private float nextChangeTime;

    private bool warnedMissingLight;
    private bool warnedNoEmissionProperty;

    private static readonly int LightPropertyId = Shader.PropertyToID("_Light");

    private void Awake()
    {
        ResolveReferences();
    }

    private void Start()
    {
        CaptureBaseline();

        currentBrightness = Random.Range(minBrightness, maxBrightness);
        targetBrightness = currentBrightness;
        nextChangeTime = Time.time + Random.Range(minChangeInterval, maxChangeInterval);
    }

    private void Update()
    {
        if (targetLight == null || hdData == null)
        {
            ResolveReferences();
            if (targetLight == null || hdData == null)
            {
                if (!warnedMissingLight)
                {
                    warnedMissingLight = true;
                    Debug.LogWarning($"[FlickeringLight] No Light found on {name}. " +
                        "Assign it in the Inspector or add it as a child.");
                }
                return;
            }
            CaptureBaseline();
        }

        // Pick a new brightness target whenever the timer runs out.
        if (Time.time >= nextChangeTime)
        {
            if (Random.value < blackoutChance)
            {
                // The wire loses contact: snap off, come back after blackoutDuration.
                currentBrightness = 0f;
                targetBrightness = 0f;
                nextChangeTime = Time.time + blackoutDuration;
            }
            else
            {
                targetBrightness = Random.Range(minBrightness, maxBrightness);
                nextChangeTime = Time.time + Random.Range(minChangeInterval, maxChangeInterval);
            }
        }

        currentBrightness = Mathf.MoveTowards(currentBrightness, targetBrightness, flickerSpeed * Time.deltaTime);
        ApplyBrightness(currentBrightness);
    }

    private void ApplyBrightness(float brightness)
    {
        if (targetLight != null && hdData != null)
            hdData.intensity = baseIntensity * brightness;

        if (hasFaceEmission)
            faceMaterial.SetFloat(LightPropertyId, baseEmission * brightness);
    }

    private void CaptureBaseline()
    {
        if (targetLight != null && hdData != null)
            baseIntensity = hdData.intensity;

        hasFaceEmission = false;
        if (plane == null) return;

        Renderer renderer = plane.GetComponent<Renderer>();
        if (renderer == null) return;

        // Runtime instance of the asset material: changes affect only this
        // renderer, not other lamps sharing the same material.
        faceMaterial = renderer.material;
        if (faceMaterial.HasProperty(LightPropertyId))
        {
            baseEmission = faceMaterial.GetFloat(LightPropertyId);
            hasFaceEmission = true;
        }
        else if (!warnedNoEmissionProperty)
        {
            warnedNoEmissionProperty = true;
            Debug.LogWarning($"[FlickeringLight] The face material {faceMaterial.name} has no _Light property, " +
                "so the face's emission cannot follow the light.");
        }
    }

    private void ResolveReferences()
    {
        if (targetLight == null)
            targetLight = FindInHierarchy<Light>(this);

        if (targetLight != null && hdData == null)
            hdData = targetLight.GetComponent<HDAdditionalLightData>();

        if (plane == null)
        {
            // Same pattern as LineLampController: prefer the child named "Plane".
            plane = transform.Find("Plane");
            if (plane == null)
            {
                Renderer renderer = FindInHierarchy<Renderer>(this);
                if (renderer != null)
                    plane = renderer.transform;
            }
        }
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
