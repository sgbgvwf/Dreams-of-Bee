using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Attach to the Player root. First-person bee flight controls (Input System + Rigidbody physics):
///   - The Look action (mouse delta) moves the view: yaw on the Player root, pitch (clamped) on the camera
///   - Hold the Fly action (W) to fly forward along the camera's look direction (look up = climb, look down = dive)
///   - Movement is physics-driven: the Rigidbody velocity is set every frame, so the bee
///     stops the instant W is released (no drift, no inertia)
///   - Gravity is off while flying; SetGravityEnabled() is a reserved hook to let gravity
///     apply temporarily (e.g. a stunned drop), only while W is not held
///   - The camera is hard-synced to the Player every frame (position + look rotation),
///     so it always follows even if it is not parented to the Player
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

    // --- Resolved references ---
    private InputAction moveAction;
    private InputAction lookAction;
    private Rigidbody rb;  // auto-added if missing - physics-driven flight requires it

    // --- Look state (accumulated in floats to avoid quaternion drift) ---
    private float yaw;    // Player rotation around the world Y axis (degrees)
    private float pitch;  // Camera rotation around the world X axis (degrees)

    // --- Reserved state ---
    private bool gravityEnabled;

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

        bool controlled = Cursor.lockState == CursorLockMode.Locked && cameraTransform != null;
        if (controlled)
            Look();

        Move(controlled);
    }

    private void LateUpdate()
    {
        // First-person: the camera always follows the Player,
        // regardless of whether it is parented to it in the hierarchy.
        if (cameraTransform == null) return;

        cameraTransform.SetPositionAndRotation(transform.position, Quaternion.Euler(pitch, yaw, 0f));
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
    /// Derive the initial yaw/pitch from the camera's current world forward,
    /// so the view in Play mode starts exactly where the camera points in the editor.
    /// </summary>
    private void CaptureInitialLook()
    {
        Vector3 forward = cameraTransform != null ? cameraTransform.forward : transform.forward;
        yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
        pitch = -Mathf.Asin(forward.y) * Mathf.Rad2Deg;
    }

    /// <summary>
    /// Apply mouse movement: yaw on the Player root (the body turns horizontally),
    /// pitch on the camera (applied together with yaw in LateUpdate), clamped to maxLookAngle.
    /// </summary>
    private void Look()
    {
        Vector2 delta = lookAction != null ? lookAction.ReadValue<Vector2>() : Vector2.zero;

        yaw += delta.x * mouseSensitivity;
        pitch -= delta.y * mouseSensitivity;  // mouse up = look up
        pitch = Mathf.Clamp(pitch, -maxLookAngle, maxLookAngle);

        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
    }

    /// <summary>
    /// Drive the Rigidbody each frame: fly forward along the camera's look direction
    /// while W is held, hover (no horizontal drift) otherwise. Gravity only applies
    /// when enabled via SetGravityEnabled and the bee is not flying.
    /// </summary>
    private void Move(bool controlled)
    {
        if (rb == null) return;

        bool flying = controlled && moveAction != null && moveAction.IsPressed();

        rb.useGravity = gravityEnabled && !flying;
        rb.angularVelocity = Vector3.zero;

        if (!flying)
        {
            // Hover: clear horizontal drift only, so a reserved-gravity fall keeps falling.
            Vector3 v = rb.velocity;
            v.x = 0f;
            v.z = 0f;
            rb.velocity = v;
            return;
        }

        rb.velocity = cameraTransform.forward * flySpeed;
    }

    /// <summary>
    /// Reserved hook: temporarily let gravity affect the bee (e.g. a stunned drop).
    /// Gravity only applies while W is not held - holding W always overrides it off.
    /// </summary>
    public void SetGravityEnabled(bool enabled)
    {
        gravityEnabled = enabled;
    }

    private void ResolveReferences()
    {
        if (cameraTransform == null)
        {
            var cam = GetComponentInChildren<Camera>(true);
            if (cam != null)
                cameraTransform = cam.transform;
        }

        if (rb == null)
            rb = GetComponent<Rigidbody>();

        if (rb == null)
        {
            rb = gameObject.AddComponent<Rigidbody>();
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            Debug.Log($"{name}: added a Rigidbody for physics-based flight. Tune its settings in the Inspector.", this);
        }
        else if (rb.interpolation == RigidbodyInterpolation.None)
        {
            // Smooth the physics steps under the first-person camera.
            rb.interpolation = RigidbodyInterpolation.Interpolate;
        }

        if (rb.isKinematic)
            Debug.LogWarning($"{name}: the Rigidbody is kinematic - velocity-based flight will not work. Uncheck 'Is Kinematic'.", this);

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
