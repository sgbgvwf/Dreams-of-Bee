using UnityEngine;

/// <summary>
/// 交互类型：物体"可交互"之后具体进行什么交互。
/// 目前唯一的交互类型是拾取（读卡器 / 门等都不是 Interactable）；
/// 后续新增交互类型时在这里扩展，并在 BeeInteractionController 的分发中加分支。
/// </summary>
public enum InteractionType
{
    /// <summary>可拾取：按交互键拾起 / 放下</summary>
    Pickup,
}

/// <summary>
/// 可交互标记组件：物体是否可交互的唯一判定来源（不再依赖层）。
/// 挂到交互物根上，例如卡片（Card 继承本组件，Type 恒为 Pickup）。
///
/// 交互流程（BeeInteractionController 统一遵守）：
///  1. 先判断是否可交互：射线命中物体（或其父级）带 Interactable 组件；
///  2. 再根据 Type 决定进行什么交互（目前只有拾取）。
///
/// 拾取物（Type == Pickup）在 Awake 时与玩家碰撞体永久 IgnoreCollision ——
/// 取代旧 Pickable 层的 IgnoreLayerCollision 规则；collider 对级的忽略不受
/// 描边切层影响，描边期间行为保持一致。
/// </summary>
public class Interactable : MonoBehaviour
{
    [SerializeField, Tooltip("交互类型（目前只有拾取）")]
    private InteractionType type = InteractionType.Pickup;

    [SerializeField, Tooltip("被携带时的朝向修正：拾取后物品旋转 = 玩家身体旋转 × 此偏移。"
        + "模型轴向各异的手持姿态（如手电光束朝向前方）在这里预先调好，一次配置所有拾取通用。")]
    private Quaternion carryRotationOffset = Quaternion.identity;

    /// <summary>交互类型。子类可覆写为固定类型（如 Card 恒为 Pickup）。</summary>
    public virtual InteractionType Type => type;

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
}
