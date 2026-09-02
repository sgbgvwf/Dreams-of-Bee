using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Serialization;

/// <summary>
/// Attach to the Player root (Body). First-person bee controls (Input System + Rigidbody physics):
///   - The Look action (mouse delta) moves the view; yaw/pitch are UNRESTRICTED (flip over,
///     loop the loop). The camera rotation is fully user-controlled and is never forced to
///     match the body.
///   - The state is re-derived every physics step from input + physical contact:
///       * Holding Space (stamina permitting) -> Flying: constant speed along the camera's
///         look direction (look where you go, W is not involved). Releasing Space, or running
///         out of stamina, switches straight to Falling - no bounce, no hover.
///       * Touching ANY object (OnCollisionStay) -> Crawling ("趴着"): the bee sticks to floors,
///         walls, ceilings and any geometry, moves along the contact surface.
///         (例外：持物时不能攀爬——拿着东西接触表面不会进入 Crawling，物品不会被强制掉落)
///       * Otherwise -> Falling: simulated gravity (Rigidbody.AddForce with
///         ForceMode.Acceleration) pulls the bee down - Unity's built-in rb.useGravity is NEVER
///         used. Gravity acts ONLY while Falling: after a release, or when stamina runs out.
///         Landing on any object switches back to Crawling.
///   - Stamina: flying drains it; EVERY other state (crawling and falling alike) restores it -
///     只要不是飞行状态就恢复体力，持物 / 坠落不会卡住恢复。
///   - The body transform is pure presentation: it aligns to the contact surface while crawling
///     and rigidly matches the camera look direction (yaw + pitch) while
///     flying/falling — 蜜蜂没有脖子,视角朝向就是身体朝向,无滞后跟随。
///   - The camera is hard-synced to the Player every frame (position + look rotation),
///     so it always follows even if it is not parented to the Player
/// Hold Alt to release the cursor (control pauses); releasing Alt re-locks it.
/// </summary>
public class BeeFlightController : MonoBehaviour
{
    public enum BeeState { Crawling, Flying, Falling }

    [Header("飞行 Flight")]
    [SerializeField, Tooltip("Forward fly speed in meters per second (hold W while flying).")]
    private float flySpeed = 8f;

    [Header("爬行 Crawling")]
    [SerializeField, Tooltip("Crawl speed along surfaces in meters per second (hold W while touching).")]
    private float crawlSpeed = 2.5f;

    [SerializeField, Tooltip("Slerp speed for aligning the body to the crawl surface (higher = snappier).")]
    private float bodyAlignSpeed = 10f;

    [Header("视角 View")]
    [SerializeField, Tooltip("Mouse look sensitivity in degrees per pixel of mouse delta.")]
    private float mouseSensitivity = 0.2f;

    [Header("重力 Gravity")]
    [SerializeField, Tooltip("Simulated gravity magnitude in m/s² (positive = pulls down).")]
    private float gravityStrength = Mathf.Abs(Physics.gravity.y);

    [SerializeField, Tooltip("Maximum fall speed in m/s. Prevents tunneling through floors.")]
    private float maxFallSpeed = 12f;

    [Header("体力 Stamina")]
    [SerializeField, Tooltip("Max stamina. Flying (holding Space) drains it, every other state (crawling or falling) restores it.")]
    private float maxStamina = 100f;

    [SerializeField, Tooltip("Stamina drained per second while flying.")]
    private float flyStaminaDrainPerSecond = 25f;

    [SerializeField, FormerlySerializedAs("crawlStaminaRegenPerSecond"), Tooltip("Stamina restored per second while not flying (crawling or falling).")]
    private float staminaRegenPerSecond = 15f;

    [Header("引用 References")]
    [SerializeField, Tooltip("Input Action asset that drives this controller. Must contain 'Fly', 'Look' and 'TakeOff' actions.")]
    private InputActionAsset inputActions;

    [SerializeField, Tooltip("Camera that provides the view and flight direction. Auto-filled with the first child Camera if empty.")]
    private Transform cameraTransform;

    // --- Resolved references ---
    private InputAction moveAction;
    private InputAction lookAction;
    private InputAction takeOffAction;
    private Rigidbody rb;  // auto-added if missing - physics-driven flight requires it
    private BeeInteractionController interaction;   // 同物体上的交互控制器：持物时禁止攀爬

    // --- Look state (accumulated in floats to avoid quaternion drift) ---
    private float yaw;    // world Y axis (degrees)
    private float pitch;  // local X axis (degrees, UNRESTRICTED - can flip over)

    // --- Contact state (physical contact = "趴着") ---
    private bool hasContact;
    private Vector3 contactNormal = Vector3.up;  // averaged contact normal, points from surface toward the bee

    // --- Stamina ---
    private float stamina;

    // --- State (re-derived every FixedUpdate) ---
    private BeeState state = BeeState.Falling;

    // --- Public API for future UI sync ---
    public float Stamina => stamina;
    public float MaxStamina => maxStamina;
    public float StaminaNormalized => maxStamina > 0f ? stamina / maxStamina : 0f;
    public BeeState CurrentState => state;
    /// <summary>Fired whenever stamina changes by more than 0.001 (param = current stamina).</summary>
    public event Action<float> StaminaChanged;
    /// <summary>Fired whenever the bee switches between Crawling/Flying/Falling.</summary>
    public event Action<BeeState> StateChanged;
    // TODO(UI): 体力 UI 尚未实现，后续用 Stamina/MaxStamina/StaminaNormalized 轮询或订阅上述事件同步。

    // --- 爬行脚步计时(音效:每步广播一次 GameEvents.CrawlStep) ---
    private float stepTimer;
    private const float stepInterval = 0.4f;   // 爬行速度 2.5 m/s,约每 1 米一步

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

        UpdateStamina(controlled);
    }

    private void FixedUpdate()
    {
        if (rb == null) return;

        // Gravity is simulated by script force (see FallStep); Unity's built-in gravity is never used.
        rb.useGravity = false;
        rb.angularVelocity = Vector3.zero;

        bool controlled = Cursor.lockState == CursorLockMode.Locked && cameraTransform != null;
        bool lifting = controlled && takeOffAction != null && takeOffAction.IsPressed();

        // Re-derive the state from input + physical contact every physics step:
        //   holding Space with stamina > 0 -> Flying; touching any object -> Crawling; else Falling.
        // 持物时不能攀爬：还拿着东西时接触任何表面都不会进入 Crawling（物品不会被强制掉落）——
        // 爬行中拿起物品，下一物理步就因持物而失去爬行状态，蜜蜂自己从表面掉下去（物品仍在手里）。
        bool holding = interaction != null && interaction.IsHolding;
        BeeState next = lifting && stamina > 0f ? BeeState.Flying
                      : hasContact && !holding ? BeeState.Crawling
                      : BeeState.Falling;
        if (next != state)
        {
            var prev = state;
            state = next;
            StateChanged?.Invoke(state);
            GameEvents.BeeStateChanged?.Invoke(prev, next);   // 注册式音效同步点(起飞/落地/坠地 + 振翅/落风循环)
        }

        switch (state)
        {
            case BeeState.Crawling: CrawlStep(); break;
            case BeeState.Flying: FlyStep(); break;
            case BeeState.Falling: FallStep(); break;
        }
    }

    private void LateUpdate()
    {
        // First-person: the camera always follows the Player,
        // regardless of whether it is parented to it in the hierarchy.
        if (cameraTransform == null) return;

        cameraTransform.SetPositionAndRotation(transform.position, GetCameraRotation());
    }

    /// <summary>While Alt is held the cursor is released (control pauses); releasing Alt re-locks it.</summary>
    private void HandleCursor()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        bool altHeld = keyboard.altKey.isPressed || keyboard.rightAltKey.isPressed;
        Cursor.lockState = altHeld ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible = altHeld;
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
    /// Apply mouse movement to yaw/pitch. Nothing is clamped: the view can
    /// flip over and loop the loop. The body rotation is NOT touched here - it is driven by
    /// the state (surface alignment while crawling, camera follow while flying/falling).
    /// </summary>
    private void Look()
    {
        Vector2 delta = lookAction != null ? lookAction.ReadValue<Vector2>() : Vector2.zero;

        yaw += delta.x * mouseSensitivity;
        pitch -= delta.y * mouseSensitivity;  // mouse up = look up
    }

    /// <summary>The camera rotation is fully user-controlled (yaw -> pitch).</summary>
    private Quaternion GetCameraRotation() => Quaternion.Euler(pitch, yaw, 0f);

    /// <summary>摄像机当前旋转（yaw → pitch）。传送门系统用它计算门后渲染相机的姿态。</summary>
    public Quaternion CameraRotation => GetCameraRotation();

    /// <summary>
    /// 传送门穿过：把玩家位姿 / 速度 / 视角按"门 A → 门 B"的相对变换映射到新关。
    /// 与传送门渲染相机共用同一套相对位姿公式（相对门的位置与玩家相对门的位置相同），
    /// 因此穿过瞬间画面天然连续。传送由 PortalDoor 触发。
    /// </summary>
    public void ApplyPortalTransform(Transform srcAnchor, Transform dstAnchor)
    {
        Quaternion relRot = dstAnchor.rotation * Quaternion.Inverse(srcAnchor.rotation);

        // 位姿 / 速度：玩家相对门 A 的偏移原样映射到门 B，速度同旋
        rb.position = dstAnchor.TransformPoint(srcAnchor.InverseTransformPoint(transform.position));
        rb.rotation = relRot * transform.rotation;
        rb.velocity = relRot * rb.velocity;
        Physics.SyncTransforms();   // 项目 AutoSyncTransforms=0：立即同步，防传送后一帧相机回跳

        // 视角映射（门均为竖直 → 实际是纯 Y 旋转；通用分解兜底，与 CaptureInitialLook 同式）
        Vector3 fwd = relRot * GetCameraRotation() * Vector3.forward;
        yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
        pitch = -Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f)) * Mathf.Rad2Deg;

        // 传送不触发 OnCollisionExit：清除陈旧接触，下一物理步按新场景重新推导状态（落回入口地面 → Crawling）
        hasContact = false;
        contactNormal = Vector3.up;
    }

    /// <summary>
    /// Camera forward derived directly from the accumulated angles. Use this instead of
    /// cameraTransform.forward inside FixedUpdate (the camera is only synced in LateUpdate).
    /// </summary>
    private Vector3 GetCameraForward() => GetCameraRotation() * Vector3.forward;

    /// <summary>
    /// Crawl along the contacted surface ('趴着'): move along the camera forward projected
    /// onto the surface plane, and align the body up to the averaged contact normal.
    /// </summary>
    private void CrawlStep()
    {
        Vector3 motionDir = Vector3.ProjectOnPlane(GetCameraForward(), contactNormal);
        if (motionDir.sqrMagnitude < 0.001f)
            motionDir = Vector3.ProjectOnPlane(transform.forward, contactNormal);  // looking straight at the surface
        if (motionDir.sqrMagnitude < 0.001f)
            return;  // fully degenerate: keep current velocity and rotation this step

        motionDir.Normalize();

        bool moving = moveAction != null && moveAction.IsPressed();
        rb.velocity = motionDir * (moving ? crawlSpeed : 0f);  // pure tangential - contact keeps the bee on the surface

        // 爬行脚步:按步距计时广播,音效系统注册监听(每步一响,可重复)
        if (moving)
        {
            stepTimer -= Time.fixedDeltaTime;
            if (stepTimer <= 0f)
            {
                stepTimer = stepInterval;
                GameEvents.CrawlStep?.Invoke();
            }
        }
        else stepTimer = 0f;

        // Align the body up to the contact surface (pure presentation; the camera is untouched).
        Quaternion target = Quaternion.LookRotation(motionDir, contactNormal);
        transform.rotation = Quaternion.Slerp(transform.rotation, target, 1f - Mathf.Exp(-bodyAlignSpeed * Time.fixedDeltaTime));
    }

    /// <summary>
    /// Fly while Space is held: constant speed along the camera's look direction
    /// (look where you go, W is not involved). Releasing Space or running out of
    /// stamina re-derives the state as Falling - gravity takes over immediately.
    /// </summary>
    private void FlyStep()
    {
        rb.velocity = GetCameraForward() * flySpeed;
        FollowCameraLook();
    }

    /// <summary>
    /// Simulated gravity: a script force pulls the bee down (never rb.useGravity).
    /// Landing on any object is detected by physical contact - the next physics step
    /// re-derives the state as Crawling automatically.
    /// </summary>
    private void FallStep()
    {
        rb.AddForce(Vector3.down * gravityStrength, ForceMode.Acceleration);  // gravityStrength is a magnitude; down is already negative Y

        // Terminal speed: one physics step (12 * 0.02 = 0.24m) is far smaller than the
        // sphere radius (0.5), so the bee can never tunnel through a surface.
        if (rb.velocity.y < -maxFallSpeed)
            rb.velocity = new Vector3(rb.velocity.x, -maxFallSpeed, rb.velocity.z);

        FollowCameraLook();
    }

    /// <summary>
    /// 蜜蜂没有脖子：视角朝向 = 身体朝向。飞行/坠落时身体刚性对准摄像机视线（yaw + pitch），
    /// 无滞后跟随——刚性携带的物品因此与视线同步转向，手电光束随抬头/低头倾斜。
    /// </summary>
    private void FollowCameraLook()
    {
        Vector3 forward = GetCameraForward();
        if (Mathf.Abs(forward.y) > 0.98f)
            return;  // near vertical - LookRotation(forward, up) would degenerate; keep current pose

        transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
    }

    /// <summary>
    /// Drain stamina while flying, restore it in every other state (crawling '趴着' and falling
    /// alike) - 只要不是飞行状态就恢复体力，持物 / 坠落都不会卡住恢复。Skipped while the cursor is free (Alt pause).
    /// </summary>
    private void UpdateStamina(bool controlled)
    {
        if (!controlled) return;

        float before = stamina;
        if (state == BeeState.Flying)
            stamina = Mathf.Max(0f, stamina - flyStaminaDrainPerSecond * Time.deltaTime);
        else
            stamina = Mathf.Min(maxStamina, stamina + staminaRegenPerSecond * Time.deltaTime);
        if (Mathf.Abs(stamina - before) > 0.001f)
            StaminaChanged?.Invoke(stamina);
    }

    /// <summary>
    /// Touching ANY object counts as crawling ('趴着'). Average all contact normals (each points
    /// from the surface toward the bee): stable in corners and independent of the view direction.
    /// </summary>
    private void OnCollisionStay(Collision collision)
    {
        Vector3 sum = Vector3.zero;
        for (int i = 0; i < collision.contactCount; i++)
            sum += collision.GetContact(i).normal;
        if (sum.sqrMagnitude > 1e-8f)
            contactNormal = sum.normalized;
        hasContact = true;
    }

    private void OnCollisionExit(Collision collision)
    {
        hasContact = false;
    }

    private void ResolveReferences()
    {
        interaction = GetComponent<BeeInteractionController>();

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
            takeOffAction = inputActions.FindAction("TakeOff");

            if (moveAction == null || lookAction == null || takeOffAction == null)
                Debug.LogWarning($"{name}: Input Action asset needs actions named 'Fly', 'Look' and 'TakeOff'.", this);
        }
        else
        {
            Debug.LogWarning($"{name}: Assign the Input Action asset to the 'Input Actions' field.", this);
        }

        stamina = maxStamina;
    }
}
