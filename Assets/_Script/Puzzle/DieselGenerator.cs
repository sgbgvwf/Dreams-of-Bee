using System.Collections;
using UnityEngine;

/// <summary>
/// 柴油机（可交互功能脚本，IInteractable 接入交互框架）：瞄准柴油机按下交互键 →
/// 手上必须拿着指定工具（扳手），否则拒绝；放行即启动，启动做三件事：
///   1) 把目标物体从场景初始位姿平滑移动到"启动后位姿"（位移 + 旋转偏移），到位后永久保持；
///   2) 把"已启动"写进跨场景镜像 GeneratorStateSO（后续关卡的消费方轮询它）；
///   3) 自身状态进存档（ISceneSaveable，单向：启动后熄不了火）。
///
/// 放行判定（在本脚本侧进行，与 CardReader.requiredItemId 同一套身份语义）：
/// 手持物身份读 PlayerStateSO 镜像（不解析玩家控制器）—— HeldItemId 与 requiredItemId
/// 相同才放行；空手 / 拿的是别的东西 → 拒绝并给"被拒"反馈（不改动任何状态、不消耗工具）。
/// requiredItemId 留空 = 不检查工具身份，瞄准就能启动。
///
/// 单向一次性机关：启动后组件自禁（enabled = false）—— 瞄准系统把禁用的交互组件视为不可交互
/// （见 BeeInteractionController），启动过的柴油机从此没有描边、也按不动，一眼看出已启动；
/// 协程不受组件禁用影响，照常把物体移到位。与 SwingSwitch 同款用法。
///
/// 移动方式（与 SwingSwitch 同一套"基线重算"思路）：首次使用时捕获目标的初始局部位姿作基线，
/// 之后每一帧从基线重算 —— 全程零漂移。位移与旋转偏移都在【目标父级空间】给，也就是
/// Inspector 里 Transform 那两个字段的坐标系，所见即所得：对着 OnDrawGizmosSelected 画的
/// 青色箭头调偏移即可。目标应为 Kinematic 刚体或无刚体 —— Transform 驱动与动态刚体物理冲突
/// （OnValidate 会警告）。移动不做碰撞检测，也不锁玩家（与 SlidingDoor / DrawerSlide 同款）。
///
/// 音效（全部注册式同步，沿用既有事件，不新增事件类型）：
/// 放行启动的瞬间 SwitchToggled（柴油机位置）；缺工具被拒 CardDeny（柴油机位置）；
/// 物体移动到位 DoorClunk（目标位置）。
///
/// 存档 = 是否已启动（单向）；恢复瞬间到位 + 按存档重写镜像，先于任何游玩帧，不走动画、不发音效。
/// 镜像 SO 是内存资产不落盘，读档后由本脚本重写一次（与 Room_02 / Room_03 八灯谜题同一套路）。
///
/// 场景可视化：选中本组件时从目标初始位姿向启动后位姿画青色箭头（尖端 = 启动后位置），
/// 尖端画黄色十字。配 Move Offset / Rotate Euler 时对着它调。
/// </summary>
public class DieselGenerator : MonoBehaviour, IInteractable, ISceneSaveable
{
    [Header("启动条件")]
    [SerializeField, Tooltip("要求的工具身份 Id：玩家手持物的 Interactable.itemId 与之相同才放行。"
        + "给扳手配 Interactable.itemId = wrench 即可。留空 = 不检查工具身份，瞄准就能启动")]
    private string requiredItemId = "wrench";

    [Header("启动后移动的物体")]
    [SerializeField, Tooltip("要移动的物体（Rigidbody 须为 Is Kinematic 或干脆没有刚体 —— Transform 驱动与动态刚体冲突）。留空 = 启动会报错并拒绝")]
    private Transform moveTarget;

    [SerializeField, Tooltip("位移偏移（目标父级空间，相对场景初始位姿）—— 与 Inspector 里 Transform 的数值同一坐标系，对着青色箭头调")]
    private Vector3 moveOffset;

    [SerializeField, Tooltip("旋转偏移（目标父级空间欧拉角，相对场景初始位姿）")]
    private Vector3 rotateEuler;

    [SerializeField, Min(0.01f), Tooltip("从初始位姿过渡到启动后位姿所用的秒数")]
    private float moveDuration = 1f;

    [Header("跨场景镜像")]
    [SerializeField, Tooltip("启动状态镜像（与后续关卡的消费方拖同一份 GeneratorStateSO 资产）")]
    private GeneratorStateSO state;

    [Header("测试")]
    [SerializeField, Tooltip("测试开关：Play 模式下勾选 = 启动柴油机（绕过工具检查，走真实移动，不是瞬移）。"
        + "单向，启动即退役，取消勾选不熄火。不是真实信号输入")]
    private bool start;

    // --- 启动状态（单向：置位后永不复位，存活于协程之间） ---
    private bool started;

    // --- 基线（首次使用捕获，目标可在场景里自由摆位） ---
    private bool hasBaseline;
    private Vector3 initialLocalPosition;
    private Quaternion initialLocalRotation;

    private bool lastStart;   // 测试开关上次驱动的值

    private void Update()
    {
        // 测试专用：Inspector 勾选"启动"的上升沿驱动启动（绕过工具检查，与真实交互分离）；
        // 启动即退役（组件自禁），此 Update 随之停摆。启动不可逆，取消勾选不熄火。
        if (start != lastStart)
        {
            lastStart = start;
            if (start) TryStart();
        }
    }

    /// <summary>交互框架入口：手持要求的工具则启动；否则拒绝（无动作，只给被拒反馈）。</summary>
    public void OnInteract()
    {
        if (started) return;   // 已启动：无动作（正常情况下组件已自禁，这里再加一道防御）
        if (!HasRequiredTool())
        {
            GameEvents.CardDeny?.Invoke(transform.position);   // 缺工具：被拒音效(借用读卡器被拒事件,注册式同步)
            return;
        }
        TryStart();
    }

    /// <summary>柴油机是否已启动（存档读取 / 后续功能判定用）。</summary>
    public bool IsStarted => started;

    /// <summary>启动柴油机（幂等：已启动直接返回 —— 单向机关熄不了火）。</summary>
    public void TryStart() => Begin(animate: true);

    /// <summary>
    /// 手持工具判定：读 PlayerStateSO 镜像的持物身份 —— 不解析玩家控制器，
    /// 也不依赖玩家场景是否已加载（镜像没写过 = 空手，判定为不满足）。
    /// 镜像由常驻的玩家场景每帧 LateUpdate 写入，"空手 ⇒ 物品 Id 恒为空"是它的不变量
    /// （见 PlayerStateSync），所以这里不会误判成"拿着工具"。
    /// </summary>
    private bool HasRequiredTool()
    {
        if (string.IsNullOrEmpty(requiredItemId)) return true;   // 未配工具身份 = 不检查
        var mirror = PlayerStateSO.Instance;
        return mirror != null && mirror.IsHolding && mirror.HeldItemId == requiredItemId;
    }

    /// <summary>
    /// 启动收尾（交互 / 测试开关 / 存档恢复三条路径的唯一汇入点）：
    /// 置位 → 捕获基线 → 写跨场景镜像 → 移动物体 → 退役。
    /// animate = true 走平滑移动 + 启动音（真实启动）；false 瞬间到位、不发音效
    /// （读档恢复先于任何游玩帧，不许有动画）。
    /// 引用没拖齐 → 报错并拒绝启动（严格非兜底，不许静默半工作）。
    /// </summary>
    private void Begin(bool animate)
    {
        if (started) return;
        if (!Validate()) return;

        started = true;
        CaptureBaseline();
        state.PushStarted(true);   // 跨场景镜像：启动瞬间写一次（读档恢复时按存档重写一次）

        if (animate)
        {
            StartCoroutine(MoveToTarget());
            GameEvents.SwitchToggled?.Invoke(transform.position);   // 启动瞬间音效(注册式同步)
        }
        else
        {
            ApplyPose(1f);   // 读档恢复：瞬间到位
        }

        // 用后即退役：自禁组件 —— 瞄准系统把禁用的交互组件视为不可交互（见 BeeInteractionController），
        // 启动过的柴油机从此无描边、按不动；协程不受 enabled 影响，照常把物体移到位。
        enabled = false;
    }

    /// <summary>严格非兜底：启动所需的引用缺失就报错并拒绝启动，不许静默半工作。</summary>
    private bool Validate()
    {
        bool ok = true;

        if (state == null)
        {
            Debug.LogError($"[DieselGenerator] {name}: State 未拖（GeneratorStateSO 资产）—— 跨场景状态写不出去，拒绝启动", this);
            ok = false;
        }
        if (moveTarget == null)
        {
            Debug.LogError($"[DieselGenerator] {name}: Move Target 未拖（启动后要移动的物体）—— 拒绝启动", this);
            ok = false;
        }
        return ok;
    }

    /// <summary>
    /// 移动协程：按进度平滑从初始位姿过渡到启动后位姿，到位精确停止
    /// （协程结束，静止时零逐帧开销）。
    /// </summary>
    private IEnumerator MoveToTarget()
    {
        float progress = 0f;
        while (progress < 1f)
        {
            // 目标随场景卸载被销毁（Unity 对象比较立即为 null）：直接退场，不再写已销毁的 Transform
            if (moveTarget == null)
                yield break;
            progress = Mathf.MoveTowards(progress, 1f, Time.deltaTime / moveDuration);
            ApplyPose(progress);
            yield return null;
        }
        // Snap to the exact endpoint（MoveTowards 已保证相等，这里显式收尾）
        ApplyPose(1f);

        GameEvents.DoorClunk?.Invoke(moveTarget.position);   // 移到位碰撞音(借用门事件,注册式同步)
    }

    private void CaptureBaseline()
    {
        initialLocalPosition = moveTarget.localPosition;
        initialLocalRotation = moveTarget.localRotation;
        hasBaseline = true;
    }

    /// <summary>
    /// 从基线重算位姿：progress 0 = 场景初始位姿，1 = 启动后位姿 —— 全程零漂移。
    /// 位移与旋转都作用在目标的父级空间（= Inspector 里 Transform 字段的坐标系）；
    /// 旋转用左乘把欧拉偏移叠加在初始姿态之上（父空间），与位移同一坐标系。
    /// </summary>
    private void ApplyPose(float progress)
    {
        if (!hasBaseline) CaptureBaseline();

        moveTarget.localPosition = Vector3.Lerp(
            initialLocalPosition, initialLocalPosition + moveOffset, progress);
        moveTarget.localRotation = Quaternion.Slerp(
            initialLocalRotation, Quaternion.Euler(rotateEuler) * initialLocalRotation, progress);
    }

    // === 存档 (ISceneSaveable:柴油机 = 是否已启动 started;恢复瞬间到位 + 重写镜像,不发音效) ===
    public string SaveableType => "DieselGenerator";

    public string CaptureToJson()
    {
        return JsonUtility.ToJson(new GeneratorState { started = started });
    }

    public void RestoreFromJson(string json)
    {
        var s = JsonUtility.FromJson<GeneratorState>(json);
        if (s == null)
        {
            Debug.LogWarning($"[DieselGenerator] {name}: 存档数据损坏，跳过柴油机状态恢复", this);
            return;
        }
        if (!s.started) return;   // 未启动：保持场景现状（组件启用，等待首次交互）

        // 已启动：在全新场景实例上瞬间到位并按存档重写镜像（恢复先于任何游玩帧，不走动画、不发音效）。
        // Begin 收尾会把组件自禁（= 已退役），与"启动过"的运行时状态一致。
        Begin(animate: false);
    }

    /// <summary>
    /// 场景可视化（选中本组件时显示，纯编辑辅助）：
    /// 基线优先用已捕获值、未进 Play 用场景当前位姿 —— 与运行时基线同源，所见即所移。
    /// 青色箭头 = 移动路径（起点 = 初始位姿，尖端 = 启动后位置）；黄色十字 = 启动后位置
    /// （位移为零、只配了旋转时也能看见终点在哪）。
    /// </summary>
    private void OnDrawGizmosSelected()
    {
        if (moveTarget == null) return;

        Vector3 p0 = hasBaseline ? initialLocalPosition : moveTarget.localPosition;
        Vector3 p1 = p0 + moveOffset;

        // 局部 → 父空间 → 世界（父级不动，移动只发生在目标自身）
        Transform parent = moveTarget.parent;
        Vector3 from = parent != null ? parent.TransformPoint(p0) : p0;
        Vector3 to = parent != null ? parent.TransformPoint(p1) : p1;

        Vector3 delta = to - from;
        if (delta.sqrMagnitude > 0f)
        {
            float len = delta.magnitude;
            Vector3 dir = delta / len;

            float headLen = Mathf.Min(0.18f, len * 0.22f);   // 箭头头部尺寸跟距离走，距离极小也不炸
            float headHalf = headLen * 0.55f;
            Vector3 headBase = to - dir * headLen;

            Vector3 up = Mathf.Abs(dir.y) < 0.99f ? Vector3.up : Vector3.right;
            Vector3 side = Vector3.Cross(dir, up).normalized;

            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(from, to);
            Gizmos.DrawLine(to, headBase + side * headHalf);
            Gizmos.DrawLine(to, headBase - side * headHalf);
        }

        const float crossSize = 0.08f;
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(to - Vector3.right * crossSize, to + Vector3.right * crossSize);
        Gizmos.DrawLine(to - Vector3.up * crossSize, to + Vector3.up * crossSize);
        Gizmos.DrawLine(to - Vector3.forward * crossSize, to + Vector3.forward * crossSize);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (state == null)
            Debug.LogWarning($"[DieselGenerator] {name}: State 未拖（GeneratorStateSO 资产）—— 启动会报错并拒绝。请在 Project 里建一份 GeneratorStateSO 拖进来", this);
        if (moveTarget == null)
            Debug.LogWarning($"[DieselGenerator] {name}: Move Target 未拖（启动后要移动的物体）—— 启动会报错并拒绝", this);
        else
        {
            var rb = moveTarget.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic)
                Debug.LogWarning($"[DieselGenerator] {name}: 目标 {moveTarget.name} 带动态 Rigidbody，Transform 驱动会与物理冲突 —— 请改为 Is Kinematic 或去掉刚体", this);
        }
        if (moveOffset == Vector3.zero && rotateEuler == Vector3.zero)
            Debug.LogWarning($"[DieselGenerator] {name}: 位移与旋转都是零 —— 启动了也没有物体移动。请配 Move Offset / Rotate Euler", this);
        if (GetComponentInParent<Interactable>() != null)
            Debug.LogWarning($"[DieselGenerator] {name}: 与 Interactable（可拾取物）挂在一起 —— 拾取流程会优先接管，交互按不动柴油机。请去掉其一", this);
        var other = GetComponentInParent<IInteractable>();
        if (other != null && !ReferenceEquals(other, this))
            Debug.LogWarning($"[DieselGenerator] {name}: 与另一个 IInteractable（按式交互物）在同一条瞄准链上 —— 按一次分发给谁不确定。柴油机应自成一体，不共链", this);
    }
#endif
}
