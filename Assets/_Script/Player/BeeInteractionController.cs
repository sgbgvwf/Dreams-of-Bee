using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Attach to the Player root next to BeeFlightController. First-person interaction:
///   - Every frame a fixed-length ray is cast from the camera center (screen center) forward
///   - Interactable-first flow: the first hit (excluding the player itself) is checked for an
///     Interactable component; no Interactable → not interactable → nothing happens
///   - The aimed Interactable is pushed to the Outline layer (描边由这条射线统一触发，
///     OutlineRangeDetector 只负责切层)：准星对准就描边，移开 / 超出射程自动恢复
///   - On the 'Interact' press (right mouse), the interaction is dispatched by the aimed
///     Interactable's type - currently only Pickup: pick up the item (its gravity is disabled,
///     its motion cleared, and its colliders turned off, and it is forced to hang slightly
///     below the player, facing the player's direction); pressing again drops it (gravity and
///     colliders restored, motion cleared)
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
    /// 第一个命中（排除玩家）的物体带 Interactable → 推入 Outline 层描边；
    /// 移开 / 超出射程 / 命中不可交互物 → 恢复上一目标的原层。
    /// </summary>
    private void UpdateAimOutline()
    {
        int mask = ~(1 << gameObject.layer);   // 排除玩家自身碰撞体
        bool hitTarget = Physics.Raycast(cameraTransform.position, cameraTransform.forward,
            out RaycastHit hit, interactRange, mask);
        var interactable = hitTarget ? hit.collider.GetComponentInParent<Interactable>() : null;

        if (interactable != null)
        {
            // 同一物体（可能命中不同 collider）：描边不变，仅刷新拾取引用
            bool sameRoot = interactable.gameObject == currentAimRoot;
            currentAim = hit.collider.transform;
            if (sameRoot)
                return;

            if (currentAimRoot != null)
                OutlineRangeDetector.Instance?.RemoveOutline(currentAimRoot);
            currentAimRoot = interactable.gameObject;
            OutlineRangeDetector.Instance?.AddOutline(currentAimRoot);
        }
        else if (currentAimRoot != null)
        {
            OutlineRangeDetector.Instance?.RemoveOutline(currentAimRoot);
            currentAim = null;
            currentAimRoot = null;
        }
    }

    private void LateUpdate()
    {
        // Carry: force the item to hang slightly below the player, facing the player's direction.
        if (heldObject == null) return;

        heldObject.position = transform.position + Vector3.down * carryHeight;
        heldObject.rotation = transform.rotation;
    }

    /// <summary>
    /// 交互判定与分发（复用每帧瞄准检测的结果，先判断是否可交互，再按类型行事）：
    ///  - 准星未瞄准可交互物 → 直接返回
    ///  - 有 Interactable → 按 Type 分发。目前只有 Pickup（拾起）；
    ///    后续新增交互类型时在这里加分支，并在 InteractionType 枚举中扩展。
    /// </summary>
    private void TryInteract()
    {
        if (currentAim == null)
            return;   // 未瞄准可交互物

        var interactable = currentAim.GetComponentInParent<Interactable>();
        if (interactable == null)
            return;

        switch (interactable.Type)
        {
            case InteractionType.Pickup:
                PickUp(currentAim);
                break;
        }
    }

    /// <summary>
    /// Disable the item's gravity, clear its motion, and snap it to the carry position below the player.
    /// </summary>
    private void PickUp(Transform target)
    {
        heldObject = target;

        heldRb = heldObject.GetComponent<Rigidbody>();
        if (heldRb != null)
        {
            heldWasGravity = heldRb.useGravity;
            heldRb.useGravity = false;
            ClearMotion();
        }

        // Turn off the colliders so the carried item never bumps into anything.
        heldColliders = heldObject.GetComponentsInChildren<Collider>(true);
        foreach (var c in heldColliders)
            c.enabled = false;

        // Snap it into the carry position immediately (LateUpdate keeps it there).
        heldObject.position = transform.position + Vector3.down * carryHeight;
        heldObject.rotation = transform.rotation;
    }

    /// <summary>Release the item where it is: restore its gravity and colliders, and clear its motion.</summary>
    private void DropHeld()
    {
        if (heldRb != null)
        {
            heldRb.useGravity = heldWasGravity;
            ClearMotion();
        }

        foreach (var c in heldColliders)
            if (c != null)
                c.enabled = true;

        heldColliders = null;
        heldObject = null;
        heldRb = null;
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
    /// 玩家在 Persistance 启动场景常驻，先于所有关卡交互物 Awake，时序有保证。
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
