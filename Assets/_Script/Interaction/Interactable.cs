using UnityEngine;

/// <summary>
/// 交互类型：物体"可交互"之后具体进行什么交互（分发在 BeeInteractionController，按类型行事）。
/// 读卡器 / 门不是 Interactable —— 它们是触发式的，不走这里。
/// 后续新增交互类型时在这里扩展，并在 BeeInteractionController 的分发中加分支。
/// </summary>
public enum InteractionType
{
    /// <summary>可拾取：按交互键拾起 / 放下。持物时按 = 放下手中物（一次只能拿一个）。</summary>
    Pickup,

    /// <summary>工具操作：只能被手里拿着的东西操作（柴油机要扳手即此类）。
    /// 空手时不算可交互 —— 不描边、按键不分发（瞄准解析里就挡掉，见 BeeInteractionController.ResolveAimTarget）；
    /// 持物时按交互键 → 这次按键交给它的功能脚本（IInteractable.OnInteract）用手中物操作，手中物不放下，
    /// 手里拿的是不是它要的东西由功能脚本自己判定。</summary>
    ToolOperated,
}

/// <summary>
/// 可交互标记组件：物体是否可交互的唯一判定来源（不再依赖层）。
/// 挂到交互物根上，例如卡片（Card 继承本组件，Type 恒为 Pickup）。
///
/// 交互流程（BeeInteractionController 统一遵守）：
///  1. 先判断是否可交互：射线命中物体（或其父级）带 Interactable 组件；
///  2. 再根据 Type 决定进行什么交互：Pickup = 拾起 / 放下；
///     ToolOperated = 交给功能脚本用手中物操作（空手时不算可交互，不描边、按不动）。
///
/// 拾取物（Type == Pickup）在 Awake 时与玩家碰撞体永久 IgnoreCollision ——
/// 取代旧 Pickable 层的 IgnoreLayerCollision 规则；collider 对级的忽略不受
/// 描边切层影响，描边期间行为保持一致。
/// </summary>
public class Interactable : MonoBehaviour, ISceneSaveable
{
    [SerializeField, Tooltip("交互类型（目前只有拾取）")]
    private InteractionType type = InteractionType.Pickup;

    [SerializeField, Tooltip("被携带时的朝向修正：拾取后物品旋转 = 玩家身体旋转 × 此偏移。"
        + "模型轴向各异的手持姿态（如手电光束朝向前方）在这里预先调好，一次配置所有拾取通用。")]
    private Quaternion carryRotationOffset = Quaternion.identity;

    [SerializeField, Tooltip("物品唯一 Id（跨场景/后续功能识别用，如存档、状态判定。留空 = 不参与身份追踪）。"
        + "现有物体不填则保持原样，不要求逐个配置。")]
    private string itemId = "";

    /// <summary>交互类型。子类可覆写为固定类型（如 Card 恒为 Pickup）。</summary>
    public virtual InteractionType Type => type;

    /// <summary>物品身份 Id（空串 = 未配置，不参与追踪）。PlayerStateSync 持物镜像读取。</summary>
    public string ItemId => itemId;

    /// <summary>被携带时叠加到身体旋转上的朝向偏移（BeeInteractionController 拾取时读取并应用）。</summary>
    public Quaternion CarryRotationOffset => carryRotationOffset;

    /// <summary>玩家碰撞体，由 BeeInteractionController 在 Awake 时注入（玩家常驻，先于任何关卡交互物）。</summary>
    public static Collider PlayerCollider { get; set; }

    protected virtual void Awake()
    {
        // 拾取物永久不与玩家碰撞：静止、被携带、玩家撞上去都算（沿用旧 Pickable 层的规则）
        if (Type == InteractionType.Pickup && PlayerCollider != null)
            ApplyCollisionIgnore();
    }

    private void ApplyCollisionIgnore()
    {
        foreach (var c in GetComponentsInChildren<Collider>(true))
        {
            if (c == null || !c.enabled || c == PlayerCollider)
                continue;
            Physics.IgnoreCollision(c, PlayerCollider, true);
        }
    }

    /// <summary>
    /// 玩家碰撞体注入后，对已 Awake 的拾取物补做碰撞忽略。
    /// 兜底时序：玩家场景与关卡并行加载时玩家可能晚于关卡激活，此时拾取物 Awake 时
    /// PlayerCollider 还是 null，等 BeeInteractionController 注入后再补（幂等，重复调用无副作用）。
    /// </summary>
    public static void ReapplyPlayerCollisionIgnore()
    {
        if (PlayerCollider == null) return;
        foreach (var interactable in FindObjectsByType<Interactable>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (interactable.Type == InteractionType.Pickup)
                interactable.ApplyCollisionIgnore();
    }

    // === 存档 (ISceneSaveable:拾取物 = 世界位姿;恢复发生在全新场景实例上,先于任何游玩帧) ===
    public string SaveableType => "Interactable";

    public string CaptureToJson()
    {
        if (Type != InteractionType.Pickup) return "";
        return JsonUtility.ToJson(new PickupState
        {
            position = transform.position,
            rotation = transform.rotation,
            localScale = transform.localScale,
        });
    }

    public void RestoreFromJson(string json)
    {
        if (Type != InteractionType.Pickup) return;
        var s = JsonUtility.FromJson<PickupState>(json);
        if (s == null)
        {
            Debug.LogWarning($"[Interactable] {name}: 存档数据损坏，跳过拾取物状态恢复", this);
            return;
        }
        // 刚体物体(卡片等):刚体位姿与 Transform 分开存储,两处都写,防物理步回跳
        transform.SetPositionAndRotation(s.position, s.rotation);
        transform.localScale = s.localScale;
        var rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.position = s.position;
            rb.rotation = s.rotation;
        }
        // 碰撞忽略已在全新实例的 Awake 建立;玩家若晚于关卡加载,由 ReapplyPlayerCollisionIgnore 兜底
    }
}
