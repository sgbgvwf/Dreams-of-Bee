using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// Attach to the LineLamp root. Drives one Area Light from the Plane child's transform:
///   - Light size always equals the Plane's size (localScale x 10)
///   - Plane.localRotation makes the light orbit around the Plane + follow its orientation
///   - Inspector fields control color / intensity / range, mirrored to the Plane's material:
///     _Color follows the light color, and the _Light emission float equals the light
///     intensity divided by emissionDivisor
/// In Play Mode the face material is a per-renderer runtime instance, so other
/// objects sharing the same material asset are unaffected. In Edit Mode the
/// shared asset itself is written, so the authored look persists in the scene.
/// Works in Edit Mode and Play Mode.
/// Do not combine with FlickeringLight / LightColorAlternator on the same lamp -
/// this script owns the light's color/intensity.
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

    [SerializeField, Tooltip("The face material's _Light emission equals the light intensity divided by this value. The default lamp setup (intensity 1000, _Light 2) needs 500.")]
    [Min(0.001f)]
    private float emissionDivisor = 500f;

    // The built-in Plane mesh is 10x10 units, so the light size is the
    // Plane's local scale multiplied by this factor.
    private const float PlaneToLightScaleFactor = 10f;

    // --- Baseline state (captured at Awake / Rebaseline) ---
    private Quaternion initialPlaneRotation;
    private Vector3 initialLightPosition;
    private Quaternion initialLightRotation;
    private HDAdditionalLightData hdData;

    private bool hasBaseline;
    private bool warnedMissingReference;
    private bool warnedMissingLightProperty;

    // --- Last-applied values: only write when changed, so the scene/material
    // don't get dirtied every editor tick. ---
    private Quaternion lastAppliedDelta = Quaternion.identity;
    private Vector2 lastAppliedSize;
    private bool hasAppliedLightProperties;
    private Color lastAppliedLightColor;
    private float lastAppliedLightIntensity;
    private float lastAppliedLightRange;
    private bool hasAppliedMaterialColor;
    private Color lastAppliedMaterialColor;
    private bool hasAppliedEmission;
    private float lastAppliedEmission;

    // The Plane's material to sync. Always derived from the Plane's renderer:
    // the runtime instance in Play Mode, the shared asset in Edit Mode.
    private Material planeMaterial;

    private static readonly int ColorPropertyId = Shader.PropertyToID("_Color");
    private static readonly int LightPropertyId = Shader.PropertyToID("_Light");

    private void Awake()
    {
        ResolveReferences();
        CaptureBaseline();
    }

    private void OnEnable()
    {
        // Always re-resolve: entering Play Mode must switch the face material
        // from the shared asset to a per-renderer runtime instance.
        ResolveReferences();
        if (!hasBaseline)
            CaptureBaseline();

        // Forget last-applied values so the freshly resolved material (and the
        // light, whose serialized state is restored after Play Mode) get a
        // fresh application on the next tick.
        hasAppliedLightProperties = false;
        hasAppliedMaterialColor = false;
        hasAppliedEmission = false;

#if UNITY_EDITOR
        // Edit Mode: drive from the editor tick so the sync works even when
        // Update doesn't fire (prefab editing, Inspector-only changes, etc.).
        EditorApplication.update += EditorTick;
#endif
    }

    private void OnDisable()
    {
#if UNITY_EDITOR
        EditorApplication.update -= EditorTick;
#endif
    }

    // Play Mode path (Update also fires in Edit Mode sometimes; caching makes it harmless).
    private void Update()
    {
        if (Application.isPlaying)
            SyncFromPlane();
    }

#if UNITY_EDITOR
    // Edit Mode path: runs every editor tick, regardless of scene view repaints.
    private void EditorTick()
    {
        if (Application.isPlaying) return;   // Play Mode is handled by Update
        SyncFromPlane();
    }
#endif

    private void SyncFromPlane()
    {
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
    /// Fired whenever an Inspector field changes — applies everything immediately,
    /// even in Edit Mode without waiting for the next editor tick.
    /// </summary>
    private void OnValidate()
    {
        ResolveReferences();

        if (plane == null || areaLight == null) return;

        if (!hasBaseline)
            CaptureBaseline();

        if (hdData == null)
            hdData = areaLight.GetComponent<HDAdditionalLightData>();

        if (syncEnabled)
        {
            SyncTransform();
            SyncScale();
        }
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
        initialLightPosition = areaLight.transform.localPosition;
        initialLightRotation = areaLight.transform.localRotation;
        hdData = areaLight.GetComponent<HDAdditionalLightData>();

        ResolveFaceMaterial();

        hasBaseline = true;
    }

    /// <summary>
    /// Derive the face material from the Plane's renderer: the per-renderer
    /// runtime instance in Play Mode (other lamps sharing the material asset
    /// are unaffected), the shared asset in Edit Mode (authored look persists).
    /// </summary>
    private void ResolveFaceMaterial()
    {
        if (plane == null)
        {
            planeMaterial = null;
            return;
        }

        Renderer renderer = plane.GetComponent<Renderer>();
        if (renderer == null)
        {
            planeMaterial = null;
            return;
        }

        planeMaterial = Application.isPlaying ? renderer.material : renderer.sharedMaterial;
    }

    private void ResolveReferences()
    {
        if (plane == null)
            plane = transform.Find("Plane");

        ResolveFaceMaterial();

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

        if (delta == lastAppliedDelta) return;
        lastAppliedDelta = delta;

        areaLight.transform.localPosition = delta * initialLightPosition;
        areaLight.transform.localRotation = delta * initialLightRotation;
    }

    /// <summary>
    /// Make the light size exactly equal to the Plane's size:
    /// width  = |localScale.x| * 10, height = |localScale.z| * 10.
    /// </summary>
    private void SyncScale()
    {
        Vector2 size = new Vector2(
            Mathf.Abs(plane.localScale.x) * PlaneToLightScaleFactor,
            Mathf.Abs(plane.localScale.z) * PlaneToLightScaleFactor);

        if (size == lastAppliedSize) return;
        lastAppliedSize = size;

        areaLight.areaSize = size;
        if (hdData != null)
        {
            hdData.shapeWidth = size.x;
            hdData.shapeHeight = size.y;
        }
    }

    /// <summary>
    /// Apply color / intensity / range from the Inspector fields, and mirror
    /// them to the face material: _Color follows the light color, _Light
    /// (emission) follows the light intensity.
    /// </summary>
    private void ApplyLightProperties()
    {
        // Light color / intensity / range — write only when changed.
        bool lightChanged = !hasAppliedLightProperties
            || lightColor != lastAppliedLightColor
            || lightIntensity != lastAppliedLightIntensity
            || lightRange != lastAppliedLightRange;

        if (lightChanged)
        {
            hasAppliedLightProperties = true;
            lastAppliedLightColor = lightColor;
            lastAppliedLightIntensity = lightIntensity;
            lastAppliedLightRange = lightRange;

            if (hdData != null)
            {
                hdData.EnableColorTemperature(false);
                hdData.SetColor(lightColor);
                hdData.intensity = lightIntensity;
            }

            areaLight.color = lightColor;
            areaLight.range = lightRange;
        }

        if (planeMaterial == null)
            ResolveFaceMaterial();

        if (planeMaterial == null) return;

        // Face color follows the light color.
        if (!hasAppliedMaterialColor || lightColor != lastAppliedMaterialColor)
        {
            planeMaterial.SetColor(ColorPropertyId, lightColor);
            lastAppliedMaterialColor = lightColor;
            hasAppliedMaterialColor = true;
        }

        // Face emission = light intensity / emissionDivisor. Simple direct
        // mapping: the face's _Light float is just the light brightness
        // scaled down into the shader's emission range.
        if (planeMaterial.HasProperty(LightPropertyId))
        {
            if (emissionDivisor > 0f)
            {
                float targetEmission = lightIntensity / emissionDivisor;
                if (!hasAppliedEmission || targetEmission != lastAppliedEmission)
                {
                    hasAppliedEmission = true;
                    lastAppliedEmission = targetEmission;
                    planeMaterial.SetFloat(LightPropertyId, targetEmission);
                }
            }
        }
        else if (!warnedMissingLightProperty)
        {
            warnedMissingLightProperty = true;
            Debug.LogWarning($"[LineLampController] The plane material {planeMaterial.name} has no _Light property, " +
                "so the face's emission cannot follow the light intensity.");
        }
    }
}
