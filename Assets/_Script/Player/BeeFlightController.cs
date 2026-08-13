using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Attach to the Player root. First-person bee flight controls (Input System):
///   - The Look action (mouse delta) moves the view: yaw on the Player root, pitch (clamped) on the child camera
///   - Hold the Fly action (W) to fly forward along the camera's look direction (look up = climb, look down = dive)
///   - No gravity, no other movement keys - the bee flies where you look
/// ESC releases the cursor and pauses control; left-click re-locks it.
/// </summary>
public class BeeFlightController : MonoBehaviour
{
    [SerializeField, Tooltip("Forward fly speed in meters per second (hold W).")]
    private float flySpeed = 8f;

    [SerializeField, Tooltip("Mouse look sensitivity in degrees per pixel of mouse delta.")]
    private float mouseSensitivity = 0.2f;

    [SerializeField, Tooltip("Maximum vertical look angle in degrees. Prevents flipping over when looking up/down.")]
    private float maxLookAngle = 89f;

    [SerializeField, Tooltip("Input Action asset that drives this controller. Must contain a 'Fly' button action and a 'Look' value action (mouse delta).")]
    private InputActionAsset inputActions;

    [SerializeField, Tooltip("Camera that provides the view and flight direction. Auto-filled with the first child Camera if empty.")]
    private Transform cameraTransform;

    // --- Resolved actions (found by name in inputActions) ---
    private InputAction moveAction;
    private InputAction lookAction;

    // --- Look state (accumulated in floats to avoid quaternion drift) ---
    private float yaw;    // Player rotation around the world Y axis (degrees)
    private float pitch;  // Camera rotation around its local X axis (degrees)

    private void Awake()
    {
        ResolveReferences();
    }

    private void OnEnable()
    {
        if (inputActions != null)
            inputActions.Enable();
    }

    private void OnDisable()
    {
        if (inputActions != null)
            inputActions.Disable();
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
        if (cameraTransform == null) return;

        Look();
        Move();
    }

    /// <summary>Release the cursor with ESC; re-lock it with a left click.</summary>
    private void HandleCursor()
    {
        var keyboard = Keyboard.current;
        var mouse = Mouse.current;

        if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
        else if (mouse != null && mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked)
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
        Vector2 delta = lookAction != null ? lookAction.ReadValue<Vector2>() : Vector2.zero;

        yaw += delta.x * mouseSensitivity;
        pitch -= delta.y * mouseSensitivity;  // mouse up = look up
        pitch = Mathf.Clamp(pitch, -maxLookAngle, maxLookAngle);

        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        cameraTransform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }

    /// <summary>Fly forward along the camera's look direction while W is held.</summary>
    private void Move()
    {
        if (moveAction == null || !moveAction.IsPressed()) return;

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

        if (inputActions != null)
        {
            moveAction = inputActions.FindAction("Fly");
            lookAction = inputActions.FindAction("Look");

            if (moveAction == null || lookAction == null)
                Debug.LogWarning($"{name}: Input Action asset needs actions named 'Fly' and 'Look'.", this);
        }
        else
        {
            Debug.LogWarning($"{name}: Assign the Input Action asset to the 'Input Actions' field.", this);
        }
    }
}
