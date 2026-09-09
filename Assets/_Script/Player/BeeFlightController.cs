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
///         持物也能趴着:拿着东西接触表面同样进入 Crawling（停住、可恢复体力），
///         但持物爬行移动速度恒为 0 —— 拿东西只能原地趴在表面上，按 W 不会前进。
///       * Otherwise -> Falling: simulated gravity (Rigidbody.AddForce with
///         ForceMode.Acceleration) pulls the bee down - Unity's built-in rb.useGravity is NEVER
///         used. Gravity acts ONLY while Falling: after a release, or when stamina runs out.
///         Landing on any object switches back to Crawling.
///   - Stamina: flying drains it; ONLY crawling restores it - Falling is neutral, so an empty
///     tank cannot be fluttered back mid-air: land on a surface, crawl, then take off again.
///     持物趴着同样恢复体力(持物时爬行速度为 0,仍能趴住回蓝后再起飞)。
///   - The body transform is pure presentation: it aligns to the contact surface while crawling
///     and rigidly matches the camera look direction (yaw + pitch) while
///     flying/falling — 蜜蜂没有脖子,视角朝向就是身体朝向,无滞后跟随。
///   - The camera is hard-synced to the Player every frame (position + look rotation),
///     so it always follows even if it is not parented to the Player
/// 光标:进入游玩由 Start 锁定,暂停菜单放 / 恢复锁由 PauseMenu 管理 —— 无 Alt 临时释放。
/// </summary>
public class BeeFlightController : MonoBehaviour
{
    public enum BeeState { Crawling, Flying, Falling }

    [Header("飞行 Flight")]
    [SerializeField, Tooltip("Forward fly speed when empty-handed, in meters per second (hold Space while flying).")]
    private float flySpeed = 5f;

    [SerializeField, Tooltip("Forward fly speed while carrying an item, in m/s. 持物负重飞行更慢。")]
    private float flySpeedWhileHolding = 3f;

    [Header("爬行 Crawling")]
    [SerializeField, Tooltip("Crawl speed along surfaces in meters per second (hold W while touching).")]
    private float crawlSpeed = 2f;

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
    [SerializeField, Tooltip("Max stamina. Flying (holding Space) drains it; ONLY crawling restores it - falling does not.")]
    private float maxStamina = 100f;

    [SerializeField, Tooltip("Stamina drained per second while flying.")]
    private float flyStaminaDrainPerSecond = 25f;

    [SerializeField, FormerlySerializedAs("crawlStaminaRegenPerSecond"), Tooltip("Stamina restored per second while crawling (趴着). 只有趴着才恢复体力,坠落不恢复。")]
    private float staminaRegenPerSecond = 15f;

    [Header("引用 References")]
    [SerializeField, Tooltip("Camera that provides the view and flight direction. Auto-filled with the first child Camera if empty.")]
    private Transform cameraTransform;

    // --- Resolved references ---
    private InputAction moveAction;
    private InputAction lookAction;
    private InputAction takeOffAction;
    private Rigidbody rb;  // auto-added if missing - physics-driven flight requires it
    private BeeInteractionController interaction;   // 同物体上的交互控制器：持物爬行速度为 0（IsHolding 查询）

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

    /// <summary>落水救援 / 未来剧情接管期间锁定玩家输入:不响应移动(爬行 / 起飞)与视角。
    /// 只锁输入不冻结世界 —— 时间与物理照常流动,蜜蜂会悬停或趴在表面;接管方负责结束时复位。</summary>
    public bool InputLocked { get; set; }

    /// <summary>Fired whenever stamina changes by more than 0.001 (param = current stamina).</summary>
    public event Action<float> StaminaChanged;
    /// <summary>Fired whenever the bee switches between Crawling/Flying/Falling.</summary>
    public event Action<BeeState> StateChanged;
    // TODO(UI): 体力 UI 尚未实现，后续用 Stamina/MaxStamina/StaminaNormalized 轮询或订阅上述事件同步。

    // --- 爬行脚步计时(音效:每步广播一次 GameEvents.CrawlStep) ---
    private float stepTimer;
    private const float stepInterval = 0.5f;   // 爬行速度 2 m/s,约每 1 米一步

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
        // 暂停菜单 / 落水救援(InputLocked)时让出控制:输入 / 体力结算靠 controlled(光标锁定)
        // 门控,timeScale=0 已冻结物理与增量。光标由 PauseMenu(暂停放 / 恢复锁)与 Start(进游玩锁)管理。
        if (PauseMenu.IsPaused || InputLocked) return;

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

        bool controlled = !InputLocked && Cursor.lockState == CursorLockMode.Locked && cameraTransform != null;
        bool lifting = controlled && takeOffAction != null && takeOffAction.IsPressed();

        // Re-derive the state from input + physical contact every physics step:
        //   holding Space with stamina > 0 -> Flying; touching any object -> Crawling; else Falling.
        // 持物不限制状态：拿着东西接触表面同样进入 Crawling（能趴住、能回体力），
        // 只是爬行移动速度为 0（见 CrawlStep）——持物不会再被迫坠落。
        BeeState next = lifting && stamina > 0f ? BeeState.Flying
                      : hasContact ? BeeState.Crawling
                      : BeeState.Falling;
        if (next != state)
        {
            var prev = state;
            state = next;
            StateChanged?.Invoke(state);
            GameEvents.BeeStateChanged?.Invoke(prev, next);   // 注册式音效同步点(起飞/落地/坠地 + 落风循环)
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
    /// 读档恢复（流程在关卡场景加载完成、恢复场景物件之后调用，任何游玩帧之前）：
    /// 从存档快照还原刚体位姿 / 速度 / 视线 / 体力，并清掉陈旧接触 ——
    /// 状态由下一物理步按新场景重新推导（通常落回入口地面 → Crawling），不广播状态事件。
    /// </summary>
    public void RestoreFromSnapshot(PlayerSnapshot s)
    {
        if (rb == null) return;

        rb.position = s.position;
        rb.rotation = s.rotation;
        rb.velocity = s.velocity;
        transform.localScale = s.localScale;

        // 视线:yaw / pitch 从存档视线分解(与 CaptureInitialLook / ApplyPortalTransform 同式)
        Vector3 fwd = s.lookRotation * Vector3.forward;
        yaw = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
        pitch = -Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f)) * Mathf.Rad2Deg;

        stamina = Mathf.Clamp(s.stamina, 0f, maxStamina);
        StaminaChanged?.Invoke(stamina);   // HUD 立即同步一次,不等下一次体力结算

        // 传送 / 恢复都不触发 OnCollisionExit:清陈旧接触,下一物理步按新场景重新推导
        hasContact = false;
        contactNormal = Vector3.up;
        state = BeeState.Falling;

        Physics.SyncTransforms();   // 项目 AutoSyncTransforms=0:立即同步,防恢复后一帧物理回跳
    }

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
    /// 传送到世界坐标(出生点落点 / 直达换场景落点 / 未来剧情传送共用)。
    /// - 只清陈旧接触不清状态:状态由下一物理步按新场景接触重推(同 ApplyPortalTransform),不广播状态事件;
    /// - yawDegrees 非空 = 重置正视朝向:只取水平角,俯仰归零 —— 出生点/落点标记的 +Z 是水平朝向,
    ///   作者手抖带俯仰也会被归零;为空 = 保持当前视角(纯移位的用法);
    /// - resetVelocity 默认清零速度(落点重启手感;要保留动量(如跌落回弹)再关掉);
    /// - 持物无需特殊处理:持物在 LateUpdate 刚性跟随玩家,跨场景时随旧场景卸载销毁,
    ///   由 BeeInteractionController.LateUpdate 的销毁守卫清空手上状态;
    /// - 相机无需额外处理:BeeFlightController.LateUpdate 每帧硬同步到刚体位姿(同帧或下一帧追上)。
    /// </summary>
    public void TeleportTo(Vector3 position, float? yawDegrees = null, bool resetVelocity = true)
    {
        if (rb == null) return;

        rb.position = position;
        if (yawDegrees.HasValue)
        {
            yaw = yawDegrees.Value;
            pitch = 0f;
            rb.rotation = Quaternion.Euler(0f, yaw, 0f);
        }
        if (resetVelocity)
            rb.velocity = Vector3.zero;

        // 传送不触发 OnCollisionExit:清陈旧接触,下一物理步按新场景接触重新推导(同 ApplyPortalTransform)
        hasContact = false;
        contactNormal = Vector3.up;

        Physics.SyncTransforms();   // 项目 AutoSyncTransforms=0:立即同步,防传送后一帧物理回跳
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

        // 持物爬行速度为 0:能趴住停靠 / 回体力,但按 W 不会前进(物品仍在手里、不会强制掉落)
        bool moving = !InputLocked && moveAction != null && moveAction.IsPressed()
                      && (interaction == null || !interaction.IsHolding);
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
    /// 持物负重飞行用 flySpeedWhileHolding（比空手慢）；持物能趴着回体力、不能爬着前进。
    /// </summary>
    private void FlyStep()
    {
        bool carrying = interaction != null && interaction.IsHolding;
        rb.velocity = GetCameraForward() * (carrying ? flySpeedWhileHolding : flySpeed);
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
    /// Stamina economy: Flying drains it; ONLY Crawling ('趴着') restores it; Falling is neutral
    /// (体力耗尽后不能在空中回蓝 - 必须落地趴着恢复才能再次起飞). Skipped while the cursor is free (pause / not in control).
    /// </summary>
    private void UpdateStamina(bool controlled)
    {
        if (!controlled) return;

        float before = stamina;
        if (state == BeeState.Flying)
            stamina = Mathf.Max(0f, stamina - flyStaminaDrainPerSecond * Time.deltaTime);
        else if (state == BeeState.Crawling)
            stamina = Mathf.Min(maxStamina, stamina + staminaRegenPerSecond * Time.deltaTime);
        // Falling: 既不消耗也不恢复
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

        // 输入统一走 GameInput:资产缺失 / 缺动作由它报错,动作返回 null → 各处空判按无该输入处理
        moveAction = GameInput.Fly;
        lookAction = GameInput.Look;
        takeOffAction = GameInput.TakeOff;

        stamina = maxStamina;
    }
}
