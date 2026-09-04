using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Attach to the Player root next to BeeFlightController. First-person interaction:
///   - Every frame a fixed-length ray is cast from the camera center (screen center) forward
///   - Interactable-first flow: the first hit (excluding the player itself) is checked for an
///     Interactable component (pickup), then for an IInteractable implementer (custom action);
///     neither → not interactable → nothing happens
///   - The aimed target is pushed to the Outline layer (描边由这条射线统一触发，
///     OutlineRangeDetector 只负责切层)：准星对准就描边，移开 / 超出射程自动恢复
///   - On the 'Interact' press (right mouse), the interaction is dispatched by component kind
///     (pickup first): Interactable → pick up the item (its gravity is disabled, its motion
///     cleared, and its colliders turned off, and it is forced to hang slightly below the
///     player, carried rigidly at a fixed body-local offset); pressing again drops it (gravity and colliders
///     restored, motion cleared). IInteractable → its OnInteract() is called; the concrete
///     behavior (flipping a lamp switch, opening a door...) is entirely up to the implementer.
///   - 拿东西时不能攀爬（任何表面）：持物状态下不会进入 Crawling 状态，物品不会被强制掉落；
///     爬行中拿起物品，蜜蜂会因无法攀爬而自己掉下去（物品仍拿在手里）。
///   - Pickups permanently ignore collision with the player's collider (set up by
///     Interactable.Awake via Interactable.PlayerCollider, replacing the old Pickable-layer rule)
/// </summary>
public class BeeInteractionController : MonoBehaviour
{
    [SerializeField, Tooltip("Length of the interaction ray cast from the camera center (screen center).")]
    private float interactRange = 3f;

    [SerializeField, Tooltip("Distance below the player where a held item is carried.")]
    private float carryHeight = 0.8f;

    [SerializeField, Tooltip("Input Action asset - must contain an 'Interact' button action (right mouse button).")]
    private InputActionAsset inputActions;

    [SerializeField, Tooltip("Camera that provides the view direction. Auto-filled with the first child Camera if empty.")]
    private Transform cameraTransform;

    // --- Resolved references ---
    private InputAction interactAction;

    // --- Aim state (每帧瞄准检测的结果，按键交互复用) ---
    private Transform currentAim;       // 当前瞄准的命中 collider（null = 未瞄准可交互物）
    private GameObject currentAimRoot;  // 描边目标：命中 collider 所属的 Interactable 根

    // --- Held-item state ---
    private Transform heldObject;
    private Collider[] heldColliders;
    private Rigidbody heldRb;
    private bool heldWasGravity;
    private Quaternion heldCarryOffset = Quaternion.identity;   // 手持姿态修正：拾取时从 Interactable.CarryRotationOffset 读取
    private string heldItemId = "";                             // 持物身份（拾取时从 Interactable.ItemId 读取，供跨场景镜像）

    /// <summary>当前是否拿着物品（BeeFlightController 借此禁止攀爬）。</summary>
    public bool IsHolding => heldObject != null;

    /// <summary>所持物品的 Id（Interactable.ItemId；空串 = 物品未配置身份或未持物）。PlayerStateSync 镜像读取。</summary>
    public string HeldItemId => heldItemId;

    /// <summary>当前所持物品的根 Transform（存档采集持物路径用；null = 未持物）。</summary>
    public Transform HeldObject => heldObject;

    /// <summary>直接拾起指定拾取物（读档恢复用：不走射线 / 交互键；手里已有东西则先放下）。</summary>
    public void ForceHold(Transform target)
    {
        if (target == null) return;
        if (heldObject != null) DropHeld();   // 防御:先放下手里的,恢复不会叠拿
        PickUp(target);
    }

    /// <summary>直接放下当前持物（收局卸载关卡前调用，防止持物引用随场景卸载销毁后悬空）。</summary>
    public void ForceDrop()
    {
        if (heldObject != null) DropHeld();
    }

    private void Awake()
    {
        ResolveReferences();
        RegisterPlayerCollider();
    }

    private void OnEnable()
    {
        // Only this action is owned here; BeeFlightController owns the whole asset.
        if (interactAction != null)
            interactAction.Enable();
    }

    private void OnDisable()
    {
        if (interactAction != null)
            interactAction.Disable();
    }

    private void Update()
    {
        if (cameraTransform == null || interactAction == null) return;
        if (Cursor.lockState != CursorLockMode.Locked) return;

        UpdateAimOutline();   // 每帧：瞄准目标变化时更新描边

        if (interactAction.WasPressedThisFrame())
        {
            if (heldObject != null)
                DropHeld();
            else
                TryInteract();
        }
    }

    /// <summary>
    /// 每帧瞄准检测（描边触发统一走这条射线）：
    /// 第一个命中（排除玩家）的物体带 Interactable（拾取物）或实现 IInteractable 的功能脚本
    /// → 推入 Outline 层描边；移开 / 超出射程 / 命中不可交互物 → 恢复上一目标的原层。
    /// </summary>
    private void UpdateAimOutline()
    {
        // 排除玩家自身与传送门门面平面（门面只显示门后场景，不可交互、不应阻挡瞄准射线）
        int mask = ~((1 << gameObject.layer) | (1 << LayerMask.NameToLayer("Portal")));
        bool hitTarget = Physics.Raycast(cameraTransform.position, cameraTransform.forward,
            out RaycastHit hit, interactRange, mask);
        // 可交互判定（拾取优先）：Interactable → 拾取物；否则 IInteractable → 自定义交互物
        var interactable = hitTarget ? hit.collider.GetComponentInParent<Interactable>() : null;
        Component aimTarget = interactable != null
            ? interactable
            : hitTarget ? hit.collider.GetComponentInParent<IInteractable>() as Component : null;

        if (aimTarget != null)
        {
            // 同一物体（可能命中不同 collider）：描边不变，仅刷新瞄准引用
            bool sameRoot = aimTarget.gameObject == currentAimRoot;
            currentAim = hit.collider.transform;
            if (sameRoot)
                return;

            if (currentAimRoot != null)
                OutlineRangeDetector.Instance?.RemoveOutline(currentAimRoot);
            currentAimRoot = aimTarget.gameObject;
            OutlineRangeDetector.Instance?.AddOutline(currentAimRoot);
            GameEvents.AimGained?.Invoke();   // 描边获得音效(切换目标只响一次,可重复)
        }
        else if (currentAimRoot != null)
        {
            OutlineRangeDetector.Instance?.RemoveOutline(currentAimRoot);
            currentAim = null;
            currentAimRoot = null;
            GameEvents.AimLost?.Invoke();   // 描边丢失音效
        }
    }

    private void LateUpdate()
    {
        // 持物被销毁（穿越传送门后物品所属旧场景卸载、或收局卸载关卡时，物品随场景一起被引擎销毁）：
        // Unity 的对象比较让 heldObject 立即等于 null，Update 的按键分发因此走不到 DropHeld ——
        // 刚体 / 碰撞体已随物体销毁，也根本没有可恢复的东西，只需清空手上状态：
        // 不清理的话 IsHolding=false 但 heldItemId 残留旧值，PlayerStateSync 镜像 / 存档
        // 会拿到"空手 + 旧持物"的矛盾状态。
        if (heldObject == null)
        {
            if (heldColliders != null)   // 非空 = 曾拾起且未正常放下 → 是随场景销毁的持物
                ClearHeldState();
            return;
        }

        // Carry: keep the item rigidly attached to the player at a fixed body-local offset.
        // 物品相对身体的位置与朝向永远不变：身体(跟随摄像机朝向 / 爬行贴面)怎么旋转，物品就跟着怎么转。
        heldObject.position = transform.TransformPoint(0f, -carryHeight, 0f);
        heldObject.rotation = transform.rotation * heldCarryOffset;
    }

    /// <summary>
    /// 持物随场景卸载被销毁后的清场：只清空手上状态（引用 / 身份），不恢复物体物理。
    /// 正常放下走 DropHeld（先恢复重力与碰撞体再清状态）；销毁路径里物体已不存在，无物可恢复。
    /// </summary>
    private void ClearHeldState()
    {
        heldColliders = null;
        heldObject = null;
        heldRb = null;
        heldWasGravity = false;
        heldCarryOffset = Quaternion.identity;
        heldItemId = "";
    }

    /// <summary>
    /// 交互判定与分发（复用每帧瞄准检测的结果，按组件类别行事，拾取优先）：
    ///  - 持物中不允许任何交互：一次只能拿一个物品，先放下才能拿下一个
    ///    （Update 的调用路径已保证，这里再加一道防御，防止未来其它路径绕过）
    ///  - 准星未瞄准可交互物 → 直接返回
    ///  - 命中物体有 Interactable → 拾起（拾取物）
    ///  - 否则有 IInteractable → 调用其 OnInteract()，具体行为由功能脚本自行处理
    /// </summary>
    private void TryInteract()
    {
        if (heldObject != null)
            return;   // 持物中：不拾取、不触发开关等交互
        if (currentAim == null)
            return;   // 未瞄准可交互物

        var interactable = currentAim.GetComponentInParent<Interactable>();
        if (interactable != null)
        {
            PickUp(currentAim);
            return;
        }

        var handler = currentAim.GetComponentInParent<IInteractable>();
        if (handler != null)
            handler.OnInteract();
    }

    /// <summary>
    /// Disable the item's gravity, clear its motion, and snap it to the carry position below the player.
    /// </summary>
    private void PickUp(Transform target)
    {
        heldObject = target;

        // 手持姿态修正：读取拾取物 Interactable 上预先配置的朝向偏移（如手电光束朝向前方）。
        // 拾取瞬间与携带全程统一应用，不同模型轴向的手持姿态一次配置永久解决。
        var interactable = target.GetComponentInParent<Interactable>();
        heldCarryOffset = interactable != null ? interactable.CarryRotationOffset : Quaternion.identity;
        heldItemId = interactable != null ? interactable.ItemId : "";   // 持物身份同步给跨场景镜像

        heldRb = heldObject.GetComponent<Rigidbody>();
        if (heldRb != null)
        {
            heldWasGravity = heldRb.useGravity;
            heldRb.useGravity = false;
            heldRb.detectCollisions = false;   // 被拾取时彻底无碰撞：碰撞体已禁用，再关刚体碰撞检测双保险
            ClearMotion();
        }

        // Turn off the colliders so the carried item never bumps into anything.
        heldColliders = heldObject.GetComponentsInChildren<Collider>(true);
        foreach (var c in heldColliders)
            c.enabled = false;

        // Snap it into the carry position immediately (LateUpdate keeps it there).
        heldObject.position = transform.TransformPoint(0f, -carryHeight, 0f);
        heldObject.rotation = transform.rotation * heldCarryOffset;

        GameEvents.PickUp?.Invoke();   // 拾取音效(注册式同步)
    }

    /// <summary>Release the item where it is: restore its gravity and colliders, and clear its motion.</summary>
    private void DropHeld()
    {
        if (heldRb != null)
        {
            heldRb.useGravity = heldWasGravity;
            heldRb.detectCollisions = true;   // 恢复碰撞检测
            ClearMotion();
        }

        foreach (var c in heldColliders)
            if (c != null)
                c.enabled = true;

        heldColliders = null;
        heldObject = null;
        heldRb = null;
        heldCarryOffset = Quaternion.identity;
        heldItemId = "";

        GameEvents.Drop?.Invoke(transform.position + Vector3.down * carryHeight);   // 掉落音效(3D,在掉落地)
    }

    /// <summary>Clear all runtime motion (velocity and angular velocity).</summary>
    private void ClearMotion()
    {
        heldRb.velocity = Vector3.zero;
        heldRb.angularVelocity = Vector3.zero;
    }

    /// <summary>
    /// 把玩家碰撞体注册给 Interactable（静态），供拾取物在 Awake 时建立
    /// 永久的 collider 对级 IgnoreCollision（取代旧 Pickable 层的 IgnoreLayerCollision 规则）。
    /// 玩家在 Player 常驻场景（启动时加载一次、永不卸载，与 Persistance 相同的机制，无 DontDestroyOnLoad），
    /// 与关卡并行加载；碰撞体注入后有 Reapply 兜底，即使玩家晚于关卡 Awake，碰撞忽略也会补上，时序不再敏感。
    /// </summary>
    private void RegisterPlayerCollider()
    {
        var collider = GetComponentInChildren<Collider>();
        if (collider == null)
        {
            Debug.LogWarning($"{name}: 未找到玩家碰撞体，拾取物将不与玩家忽略碰撞。给 Player 加一个 Collider。", this);
            return;
        }
        Interactable.PlayerCollider = collider;
        Interactable.ReapplyPlayerCollisionIgnore();   // 已 Awake 的拾取物补做碰撞忽略（幂等）
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
            interactAction = inputActions.FindAction("Interact");
            if (interactAction == null)
                Debug.LogWarning($"{name}: Input Action asset needs an 'Interact' action (bound to the right mouse button).", this);
        }
        else
        {
            Debug.LogWarning($"{name}: Assign the Input Action asset to the 'Input Actions' field.", this);
        }
    }
}
