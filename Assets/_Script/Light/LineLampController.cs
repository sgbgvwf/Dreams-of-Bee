using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Attach to the LineLamp root. Drives one Area Light from the Plane child's transform:
///   - Plane.localScale.x/z scales the area light size proportionally
///   - Plane.localRotation makes the light orbit around the Plane + follow its orientation
/// Works in Edit Mode and Play Mode.
/// </summary>
[ExecuteAlways]
public class LineLampController : MonoBehaviour
{
    [SerializeField, Tooltip("The visual Plane whose rotation/scale drives the light.")]
    private Transform plane;

    [SerializeField, Tooltip("The area light to drive. Auto-filled with the first child Area light if empty.")]
    private Light areaLight;

    [SerializeField, Tooltip("When enabled, Plane transform changes are synced to the light in real time.")]
    private bool syncEnabled = true;

    [SerializeField, Tooltip("Light color applied to the area light.")]
    private Color lightColor = Color.white;

    [SerializeField, Tooltip("Light intensity (HDRP, in the light's current unit).")]
    [Range(0f, 100000f)]
    private float lightIntensity = 1000f;

    [SerializeField, Tooltip("Light range.")]
    [Min(0f)]
    private float lightRange = 10f;

    [SerializeField, Tooltip("Plane's material whose _Color follows the light color. Auto-filled from the Plane's renderer if empty.")]
    private Material planeMaterial;

    // --- Baseline state (captured at Awake / Rebaseline) ---
    private Quaternion initialPlaneRotation;
    private Vector3 initialPlaneScale;
    private Vector3 initialLightPosition;
    private Quaternion initialLightRotation;
    private Vector2 initialAreaSize;
    private HDAdditionalLightData hdData;

    private bool hasBaseline;
    private bool warnedMissingReference;
    private bool hasAppliedMaterialColor;
    private Color lastAppliedMaterialColor;

    private static readonly int ColorPropertyId = Shader.PropertyToID("_Color");

    private void Awake()
    {
        ResolveReferences();
        CaptureBaseline();
    }

    private void OnEnable()
    {
        // Re-resolve when the component is enabled (handles domain reload, etc.)
        if (!hasBaseline)
        {
            ResolveReferences();
            CaptureBaseline();
        }
    }

    private void Update()
    {
        // Self-heal: re-resolve and re-capture if the baseline was never captured
        if (!hasBaseline)
        {
            ResolveReferences();
            CaptureBaseline();
        }

        if (!hasBaseline || !syncEnabled) return;
        SyncTransform();
        SyncScale();
        ApplyLightProperties();
    }

    /// <summary>
    /// Fired whenever an Inspector field changes — applies color/intensity/range
    /// immediately, even in Edit Mode without waiting for the next Update.
    /// </summary>
    private void OnValidate()
    {
        if (areaLight == null)
            ResolveReferences();
        if (areaLight == null) return;

        if (hdData == null)
            hdData = areaLight.GetComponent<HDAdditionalLightData>();

        ApplyLightProperties();
    }

    /// <summary>Re-capture the baseline state. Also available via the Inspector context menu.</summary>
    [ContextMenu("Rebaseline")]
    private void Rebaseline()
    {
        ResolveReferences();
        CaptureBaseline();
    }

    private void CaptureBaseline()
    {
        if (plane == null || areaLight == null)
        {
            hasBaseline = false;
            if (!warnedMissingReference)
            {
                warnedMissingReference = true;
                Debug.LogWarning($"[LineLampController] Cannot find {(plane == null ? "Plane" : "Area Light")} on {name}. " +
                    "Assign it in the Inspector or make sure the child objects are named 'Plane' / have an Area light.");
            }
            return;
        }

        initialPlaneRotation = plane.localRotation;
        initialPlaneScale = plane.localScale;
        initialLightPosition = areaLight.transform.localPosition;
        initialLightRotation = areaLight.transform.localRotation;
        initialAreaSize = areaLight.areaSize;
        hdData = areaLight.GetComponent<HDAdditionalLightData>();
        hasBaseline = true;
    }

    private void ResolveReferences()
    {
        if (plane == null)
            plane = transform.Find("Plane");

        if (planeMaterial == null && plane != null)
        {
            var renderer = plane.GetComponent<Renderer>();
            if (renderer != null)
                planeMaterial = renderer.sharedMaterial;
        }

        if (areaLight == null)
        {
            var allLights = GetComponentsInChildren<Light>(true);
            foreach (var l in allLights)
            {
                // HDRP: the built-in Light.type is unreliable (area lights serialize
                // as Point); the real type lives in HDAdditionalLightData.
                var hd = l.GetComponent<HDAdditionalLightData>();
                bool isArea = hd != null ? hd.type == HDLightType.Area : l.type == LightType.Area;
                if (isArea)
                {
                    areaLight = l;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Apply the Plane's rotation delta to the light:
    /// orbit around the Plane (localPosition) + orientation follow (localRotation).
    /// </summary>
    private void SyncTransform()
    {
        Quaternion delta = plane.localRotation * Quaternion.Inverse(initialPlaneRotation);

        areaLight.transform.localPosition = delta * initialLightPosition;
        areaLight.transform.localRotation = delta * initialLightRotation;
    }

    /// <summary>
    /// Scale the light proportionally to the Plane's X/Z scale.
    /// </summary>
    private void SyncScale()
    {
        float rx = SafeRatio(plane.localScale.x, initialPlaneScale.x);
        float rz = SafeRatio(plane.localScale.z, initialPlaneScale.z);

        Vector2 size = new Vector2(
            initialAreaSize.x * rx,
            initialAreaSize.y * rz);

        areaLight.areaSize = size;
        if (hdData != null)
        {
            hdData.shapeWidth = size.x;
            hdData.shapeHeight = size.y;
        }
    }

    /// <summary>
    /// Apply color / intensity / range from the Inspector fields.
    /// Disables color temperature mode so the color takes effect directly.
    /// </summary>
    private void ApplyLightProperties()
    {
        if (hdData != null)
        {
            hdData.EnableColorTemperature(false);
            hdData.SetColor(lightColor);
            hdData.intensity = lightIntensity;
        }

        areaLight.color = lightColor;
        areaLight.range = lightRange;

        // Keep the Plane's emissive material color in sync.
        // Only write when the color actually changed to avoid dirtying the
        // material asset every frame in Edit Mode.
        if (planeMaterial != null && (!hasAppliedMaterialColor || lightColor != lastAppliedMaterialColor))
        {
            planeMaterial.SetColor(ColorPropertyId, lightColor);
            lastAppliedMaterialColor = lightColor;
            hasAppliedMaterialColor = true;
        }
    }

    /// <summary>
    /// Returns current / initial, guarded against zero division and negative values.
    /// </summary>
    private static float SafeRatio(float current, float initial)
    {
        if (Mathf.Approximately(initial, 0f))
            return 1f;
        return Mathf.Abs(current) / Mathf.Abs(initial);
    }
}
