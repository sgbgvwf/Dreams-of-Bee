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

    /// <summary>交互类型。子类可覆写为固定类型（如 Card 恒为 Pickup）。</summary>
    public virtual InteractionType Type => type;

    /// <summary>玩家碰撞体，由 BeeInteractionController 在 Awake 时注入（玩家常驻，先于任何关卡交互物）。</summary>
    public static Collider PlayerCollider { get; set; }

    protected virtual void Awake()
    {
        // 拾取物永久不与玩家碰撞：静止、被携带、玩家撞上去都算（沿用旧 Pickable 层的规则）
        if (Type == InteractionType.Pickup && PlayerCollider != null)
        {
            foreach (var c in GetComponentsInChildren<Collider>(true))
            {
                if (c == null || !c.enabled || c == PlayerCollider)
                    continue;
                Physics.IgnoreCollision(c, PlayerCollider, true);
            }
        }
    }
}
