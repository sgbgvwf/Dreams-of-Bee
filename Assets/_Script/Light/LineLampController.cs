using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// Attach to the LineLamp root. Drives the two sibling Area Lights from the
/// Plane child's transform:
///   - Plane.localScale.x/z scales the main area light size proportionally
///   - Plane.localRotation makes both lights orbit around the Plane + follow its orientation
///   - The back light (Area Light [1]) always = main light size + 2 on each axis (1 per edge)
/// Works in Edit Mode and Play Mode.
/// </summary>
[ExecuteAlways]
public class LineLampController : MonoBehaviour
{
    [SerializeField, Tooltip("The visual Plane whose rotation/scale drives the lights.")]
    private Transform plane;

    [SerializeField, Tooltip("Area lights to drive (main + back). Auto-filled with child Area lights if empty.")]
    private Light[] areaLights;

    [SerializeField, Tooltip("When enabled, Plane transform changes are synced to the lights in real time.")]
    private bool syncEnabled = true;

    // Each edge of the back light extends 1 unit beyond the main light
    private static readonly Vector2 BackLightOffset = new Vector2(2f, 2f);

    // --- Baseline state (captured at Awake / Rebaseline) ---
    private Quaternion initialPlaneRotation;
    private Vector3 initialPlaneScale;
    private Vector3[] initialLightPositions;
    private Quaternion[] initialLightRotations;
    private Vector2 initialAreaSize0;
    private HDAdditionalLightData[] hdData;

    private bool hasBaseline;

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
        if (!hasBaseline || !syncEnabled) return;
        SyncTransform();
        SyncScale();
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
        if (plane == null || areaLights == null || areaLights.Length < 2)
        {
            hasBaseline = false;
            return;
        }

        initialPlaneRotation = plane.localRotation;
        initialPlaneScale = plane.localScale;

        int n = areaLights.Length;
        initialLightPositions = new Vector3[n];
        initialLightRotations = new Quaternion[n];
        hdData = new HDAdditionalLightData[n];

        for (int i = 0; i < n; i++)
        {
            if (areaLights[i] == null) continue;
            initialLightPositions[i] = areaLights[i].transform.localPosition;
            initialLightRotations[i] = areaLights[i].transform.localRotation;
            hdData[i] = areaLights[i].GetComponent<HDAdditionalLightData>();
        }

        initialAreaSize0 = areaLights[0].areaSize;
        hasBaseline = true;
    }

    private void ResolveReferences()
    {
        if (plane == null)
            plane = transform.Find("Plane");

        if (areaLights == null || areaLights.Length == 0)
        {
            var allLights = GetComponentsInChildren<Light>(true);
            int count = 0;
            foreach (var l in allLights)
                if (l.type == LightType.Area) count++;

            areaLights = new Light[count];
            int idx = 0;
            foreach (var l in allLights)
                if (l.type == LightType.Area)
                    areaLights[idx++] = l;
        }
    }

    /// <summary>
    /// Apply the Plane's rotation delta to both lights:
    /// orbit around the Plane (localPosition) + orientation follow (localRotation).
    /// </summary>
    private void SyncTransform()
    {
        Quaternion delta = plane.localRotation * Quaternion.Inverse(initialPlaneRotation);

        for (int i = 0; i < areaLights.Length; i++)
        {
            if (areaLights[i] == null) continue;
            areaLights[i].transform.localPosition = delta * initialLightPositions[i];
            areaLights[i].transform.localRotation = delta * initialLightRotations[i];
        }
    }

    /// <summary>
    /// Scale the main light proportionally to the Plane's X/Z scale.
    /// The back light always = main light size + BackLightOffset (one per edge).
    /// </summary>
    private void SyncScale()
    {
        float rx = SafeRatio(plane.localScale.x, initialPlaneScale.x);
        float rz = SafeRatio(plane.localScale.z, initialPlaneScale.z);

        Vector2 size0 = new Vector2(
            initialAreaSize0.x * rx,
            initialAreaSize0.y * rz);

        Vector2 size1 = size0 + BackLightOffset;

        ApplySize(0, size0);
        ApplySize(1, size1);
    }

    private void ApplySize(int index, Vector2 size)
    {
        Light l = areaLights[index];
        if (l == null) return;

        l.areaSize = size;
        if (hdData[index] != null)
        {
            hdData[index].shapeWidth = size.x;
            hdData[index].shapeHeight = size.y;
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
