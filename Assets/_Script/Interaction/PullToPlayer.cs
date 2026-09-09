using UnityEngine;

/// <summary>
/// 冲量件（可交互功能脚本，IInteractable 接入交互框架）：瞄准本物体按下交互键 →
/// 给目标物体一个 Inspector 配置好的瞬间冲量（Vector3，世界空间方向+大小，ForceMode.Impulse：
/// 轻物飞得远、重物只挪一挪，力度按 N·s 配）。一次交互给一次力，可以反复给；
/// 只作用于动态 Rigidbody。
///
/// 与框架的关系：单击 = 边缘语义 —— 描边由 BeeInteractionController 的瞄准射线统一给
/// （实现 IInteractable 即被描边），按下瞬间由 TryInteract 调 OnInteract()。
/// 本组件不自持输入、不发射线、只负责施力。旧版"按住持续拉向玩家"语义已移除
/// （自轮询 InputAction 资产、自持瞄准射线、PlayerStateSO 镜像一套全删）。
///
/// 挂法：挂在目标物体（Rigidbody 所在或其父级）上，物体要有 Collider 供瞄准命中；
/// 描边目标 = 本组件所在链的物体（一般 = 整件）。未解锁的锁物（SwingSwitch.lockedRigidbody，
/// 起步 Is Kinematic）按下会得到一次警告并忽略 —— 那是"还固定着"的显式反馈，
/// 拨开机关后再按就有效。与拾取物（Interactable）/ 别的 IInteractable 不要共挂一条瞄准链
/// （同链按一次分发给谁不确定，OnValidate 会警告）。
/// 场景可视化：选中本组件时把冲量画成箭头 —— 方向即配的方向，长度随 |冲量| 缩放，
/// 对着箭头调 Inspector 里的 Vector3 即可。
/// </summary>
public class PullToPlayer : MonoBehaviour, IInteractable
{
    [SerializeField, Tooltip("单次交互施加的冲量（N·s，世界空间方向+大小）：方向朝哪、多大劲，在这里配死")]
    private Vector3 impulse = new Vector3(0f, 100f, 0f);

    // --- 运行时 ---
    private Rigidbody rb;             // 被推的刚体（组件挂在刚体或其父级上）

    private bool warnedMissingRb;
    private bool warnedKinematic;

    private void Awake()
    {
        rb = GetComponentInParent<Rigidbody>();
    }

    /// <summary>交互框架入口：给目标一个冲量。刚体缺失 / 刚体是 Kinematic（还锁着）→ 警告一次并忽略。</summary>
    public void OnInteract()
    {
        if (rb == null)
        {
            if (!warnedMissingRb)
            {
                warnedMissingRb = true;
                Debug.LogWarning($"[PullToPlayer] {name}: 找不到 Rigidbody —— 无法施力。请把本组件放到目标物体（Rigidbody 所在或其父级）上", this);
            }
            return;
        }
        if (rb.isKinematic)
        {
            if (!warnedKinematic)
            {
                warnedKinematic = true;
                Debug.LogWarning($"[PullToPlayer] {name}: Rigidbody 是 Kinematic —— 施力无效（物体还被固定着？拨开锁定机关后再试）。请改为动态（Is Kinematic 取消勾选）", this);
            }
            return;
        }

        rb.AddForce(impulse, ForceMode.Impulse);   // 一帧冲量，之后完全交给物理
    }

    /// <summary>
    /// 场景可视化（选中本组件时）：冲量画成箭头 —— 方向 = 配的方向，长度随 |冲量| 缩放
    /// （纯编辑辅助，调 Inspector 里的 Vector3 时对着看）。
    /// </summary>
    private void OnDrawGizmosSelected()
    {
        if (impulse == Vector3.zero) return;

        Vector3 dir = impulse.normalized;
        float len = Mathf.Max(0.3f, impulse.magnitude * 0.02f);

        // 与方向垂直的参考轴（画箭头头部）
        Vector3 up = Mathf.Abs(dir.y) < 0.99f ? Vector3.up : Vector3.right;
        Vector3 side = Vector3.Cross(dir, up).normalized;

        float headLen = len * 0.18f;
        float headHalf = headLen * 0.6f;
        Vector3 tip = transform.position + dir * len;
        Vector3 headBase = tip - dir * headLen;

        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(transform.position, tip);
        Gizmos.DrawLine(tip, headBase + side * headHalf);
        Gizmos.DrawLine(tip, headBase - side * headHalf);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (GetComponentInParent<Interactable>() != null)
            Debug.LogWarning($"[PullToPlayer] {name}: 与 Interactable（可拾取物）挂在一起 —— 拾取流程会优先接管，交互按不出力。请去掉其一", this);
        var other = GetComponentInParent<IInteractable>();
        if (other != null && !ReferenceEquals(other, this))
            Debug.LogWarning($"[PullToPlayer] {name}: 与另一个 IInteractable（按式交互物）在同一条瞄准链上 —— 按一次分发给谁不确定。目标物体应自成一体，不共链", this);
        var rb = GetComponentInParent<Rigidbody>();
        if (rb == null)
            Debug.LogWarning($"[PullToPlayer] {name}: 找不到 Rigidbody —— 施力需要动态刚体。请把本组件放到目标物体（Rigidbody 所在或其父级）上", this);
        else if (rb.isKinematic)
            // Debug.LogWarning($"[PullToPlayer] {name}: Rigidbody 是 Kinematic —— 施力无效。若它是被摆动机关锁着的物体，Kinematic 起步是预期配置；否则请取消勾选", this);
        if (impulse == Vector3.zero)
            Debug.LogWarning($"[PullToPlayer] {name}: Impulse 为 0 —— 交互给了个寂寞。请在 Inspector 配方向与大小", this);
    }
#endif
}
