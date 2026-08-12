using UnityEngine;

/// <summary>
/// Attach to the Player root. First-person bee flight controls:
///   - Mouse moves the view: yaw on the Player root, pitch (clamped) on the child camera
///   - Hold W to fly forward along the camera's look direction (look up = climb, look down = dive)
///   - No gravity, no other movement keys - the bee flies where you look
/// ESC releases the cursor and pauses control; left-click re-locks it.
/// </summary>
public class BeeFlightController : MonoBehaviour
{
    [SerializeField, Tooltip("Forward fly speed in meters per second (hold W).")]
    private float flySpeed = 8f;

    [SerializeField, Tooltip("Mouse look sensitivity in degrees per input unit.")]
    private float mouseSensitivity = 2f;

    [SerializeField, Tooltip("Maximum vertical look angle in degrees. Prevents flipping over when looking up/down.")]
    private float maxLookAngle = 89f;

    [SerializeField, Tooltip("Camera that provides the view and flight direction. Auto-filled with the first child Camera if empty.")]
    private Transform cameraTransform;

    // --- Look state (accumulated in floats to avoid quaternion drift) ---
    private float yaw;    // Player rotation around the world Y axis (degrees)
    private float pitch;  // Camera rotation around its local X axis (degrees)

    private void Awake()
    {
        ResolveReferences();
    }

    private void Start()
    {
        CaptureInitialLook();
        LockCursor();
    }

    private void Update()
    {
        HandleCursor();
        if (Cursor.lockState != CursorLockMode.Locked) return;

        Look();
        Move();
    }

    /// <summary>Release the cursor with ESC; re-lock it with a left click.</summary>
    private void HandleCursor()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else if (Input.GetMouseButtonDown(0) && Cursor.lockState != CursorLockMode.Locked)
        {
            LockCursor();
        }
    }

    private void LockCursor()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    /// <summary>
    /// Derive the initial yaw/pitch from the Player's current world forward,
    /// so the view in Play mode starts exactly where the scene camera points.
    /// </summary>
    private void CaptureInitialLook()
    {
        Vector3 forward = transform.forward;
        yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        pitch = -Mathf.Asin(forward.y) * Mathf.Rad2Deg;
    }

    /// <summary>
    /// Apply mouse movement: yaw on the Player root (the body turns horizontally),
    /// pitch on the camera, clamped to maxLookAngle.
    /// </summary>
    private void Look()
    {
        yaw += Input.GetAxis("Mouse X") * mouseSensitivity;
        pitch -= Input.GetAxis("Mouse Y") * mouseSensitivity;  // mouse up = look up
        pitch = Mathf.Clamp(pitch, -maxLookAngle, maxLookAngle);

        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        cameraTransform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }

    /// <summary>Fly forward along the camera's look direction while W is held.</summary>
    private void Move()
    {
        if (!Input.GetKey(KeyCode.W)) return;

        transform.position += cameraTransform.forward * (flySpeed * Time.deltaTime);
    }

    private void ResolveReferences()
    {
        if (cameraTransform == null)
        {
            var cam = GetComponentInChildren<Camera>(true);
            if (cam != null)
                cameraTransform = cam.transform;
        }
    }
}
