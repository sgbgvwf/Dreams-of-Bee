using UnityEngine;
using System.Collections;

/// <summary>
/// Slides a door panel sideways on demand: call OpenDoor(), CloseDoor() or
/// ToggleDoor() from any script (e.g. a CardReader). The door is fully
/// reactive - it does nothing while idle; each call starts a coroutine that
/// slides the door over duration seconds, then the coroutine ends. Assign
/// the door's root Transform in the Inspector.
/// The door slides along slideDirection (default +X, "opens to the right")
/// by slideDistance units. The slideCurve maps slide progress (0..1) to
/// displacement (0..1): a straight line is constant speed, an S-curve
/// (the default) gives smooth ease-in / ease-out.
/// The closed position is captured on first use, so the door can be freely
/// posed in the scene. The script drives only the assigned door Transform,
/// so every child (the model parts, the Area Lights, LightColorAlternator on
/// a child) travels together. Safe to combine with the light scripts.
/// Attach to any GameObject and drag the door root into the Inspector.
/// Play Mode only.
/// </summary>
public class SlidingDoor : MonoBehaviour
{
    [SerializeField, Tooltip("The door's root Transform to slide. Drag it here.")]
    private Transform door;

    [SerializeField, Tooltip("Slide direction (world space). Default +X: opens to the right.")]
    private Vector3 slideDirection = Vector3.right;

    [SerializeField, Tooltip("How far the door slides (world units). Open position = closed position + slideDirection * slideDistance.")]
    [Min(0.01f)]
    private float slideDistance = 3f;

    [SerializeField, Tooltip("Seconds a full open or close takes.")]
    [Min(0.01f)]
    private float duration = 1f;

    [SerializeField, Tooltip("Maps slide progress (0..1) to displacement (0..1). Straight line = constant speed, S-curve = smooth acceleration/deceleration.")]
    private AnimationCurve slideCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [SerializeField, Tooltip("Test switch: tick it during Play Mode to open, untick to close. Not a real signal input.")]
    private bool open;

    // --- Baseline captured on first use (door may be posed in the scene) ---
    private Vector3 closedPosition;
    private Vector3 openPosition;
    private bool hasBaseline;

    // --- Slide state ---
    private float progress;     // 0 = closed, 1 = open; survives between coroutines
    private bool targetOpen;    // where the door is headed
    private Coroutine slideRoutine;

    private bool lastOpen;   // last value the test switch drove the door with
    private bool warnedMissingDoor;

    private void Update()
    {
        // Test-only: a change of the Inspector checkbox drives the door;
        // external signals (e.g. CardReader) are never overridden.
        if (open != lastOpen)
        {
            lastOpen = open;
            SignalTo(open);
        }
    }

    /// <summary>Open the door. No-op when it is already open or opening.</summary>
    public void OpenDoor()
    {
        SignalTo(true);
    }

    /// <summary>Close the door. No-op when it is already closed or closing.</summary>
    public void CloseDoor()
    {
        SignalTo(false);
    }

    /// <summary>Flip the door's state; reverses direction smoothly mid-flight.</summary>
    public void ToggleDoor()
    {
        SignalTo(!targetOpen);
    }

    private void SignalTo(bool opening)
    {
        if (door == null)
        {
            if (!warnedMissingDoor)
            {
                warnedMissingDoor = true;
                Debug.LogWarning($"[SlidingDoor] No door assigned on {name}. " +
                    "Drag the door's root Transform into the Inspector.");
            }
            return;
        }

        if (!hasBaseline) CaptureBaseline();
        if (opening == targetOpen) return;   // already headed there: no-op
        targetOpen = opening;
        StartSlide();
    }

    /// <summary>True while the door's target state is open.</summary>
    public bool IsOpen => targetOpen;

    /// <summary>Toggle from the Inspector context menu for quick Play Mode testing.</summary>
    [ContextMenu("Toggle Door")]
    private void ToggleDoorFromInspector()
    {
        ToggleDoor();
    }

    private void StartSlide()
    {
        // A new signal mid-flight cancels the old slide and continues from
        // the current progress, so a reversal turns around smoothly.
        if (slideRoutine != null) StopCoroutine(slideRoutine);
        slideRoutine = StartCoroutine(SlideTo(targetOpen));
    }

    private IEnumerator SlideTo(bool opening)
    {
        float target = opening ? 1f : 0f;
        while (progress != target)
        {
            progress = Mathf.MoveTowards(progress, target, Time.deltaTime / duration);
            door.position = Vector3.Lerp(closedPosition, openPosition, slideCurve.Evaluate(progress));
            yield return null;
        }
        // Snap to the exact endpoint; the coroutine ends here, so an idle
        // door never runs any per-frame work.
        door.position = Vector3.Lerp(closedPosition, openPosition, slideCurve.Evaluate(target));
        slideRoutine = null;
    }

    private void CaptureBaseline()
    {
        closedPosition = door.position;
        openPosition = closedPosition + slideDirection * slideDistance;
        hasBaseline = true;
    }
}
