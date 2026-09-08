using UnityEngine;
using System.Collections;

/// <summary>
/// 摆动机关（可交互功能脚本，IInteractable 接入交互框架）：交互一次，让目标物体绕铰轴
/// 从场景初始位摆到 Swing Angle 并永久保持 —— 单向一次性机关，摆过去不摆回、也开不了
/// 第二次（再交互无动作），像拨开的插销 / 翻开的盖板 / 放下的桥。
///
/// 挂法与瞄准：交互件 = 摆动体本身（Target 拖自己，自身须有 Collider 供瞄准 —— 交互框架
/// 只负责射线判定与描边，本脚本只提供行为），也可以是静止按钮（Target 另拖要摆的物体，
/// 按钮须有 Collider）—— Target 是独立引用，两种挂法同样成立，没有特殊分支。
/// Target 未配置 = 交互时报错拒绝（刻意，不兜底）。
///
/// 旋转永远从"首次使用"时捕获的目标初始位姿（基线）重算，开启过程零漂移；
/// 轴 = 目标初始姿态的自身局部轴（Rotation Axis，模型轴向未知，进 Play 试）；
/// 目标原点不在铰点时配 Pivot Offset（铰点相对目标原点的父空间偏移）。
/// 摆角带符号 = 摆向（90 与 -90 相反）。
///
/// 锁定（可选）：lockedRigidbody = 被这个机关锁住的另一个物体 —— 起步时在场景里把它设为
/// Is Kinematic（固定、不受力），机关首次摆到位的瞬间自动释放（取消 Kinematic）并永不重锁；
/// 之后它受物理支配，可被 PullToPlayer 等冲量推动。留空 = 纯摆动机关。
///
/// 用后即退役：开启瞬间组件自禁（enabled = false）—— 瞄准系统把禁用的交互组件视为
/// 不可交互（见 BeeInteractionController），开过的机关从此没有描边、也按不动，一眼看出已开；
/// 协程不受组件禁用影响，照常摆完并在到位瞬间解锁。
///
/// 注意：目标应为 Kinematic 刚体或无刚体 —— Transform 驱动与动态刚体物理冲突。
/// 音效：按下瞬间 SwitchToggled（交互件位置）；摆动到位 DoorClunk（目标位置）；
/// 解锁瞬间 DoorUnlock（被锁物位置）—— 全部注册式同步，门事件借用。
/// 存档 = 机关已开启（opened 单向）；恢复瞬间到位 + 同步解锁（先于任何游玩帧，不发音效）。
/// 场景可视化：选中本组件时 Scene 视图在目标上画出青色轴线箭头与黄色铰点十字 ——
/// 箭头 = 轴正向（正摆角按右手定则绕它旋转），十字 = 铰点（Pivot Offset 生效处）。
/// 配 Rotation Axis / Pivot Offset 时对着箭头调即可。
/// </summary>
public class SwingSwitch : MonoBehaviour, IInteractable, ISceneSaveable
{
    public enum RotAxis { X, Y, Z }

    [Header("目标与铰轴")]
    [SerializeField, Tooltip("要摆动的物体（转门/翻板…）。留空 = 交互时报错拒绝。交互件本身就是转动体时拖自己")]
    private Transform target;

    [SerializeField, Tooltip("绕目标初始姿态的哪个局部轴摆（模型轴向未知，进 Play 试）")]
    private RotAxis rotationAxis = RotAxis.Y;

    [SerializeField, Tooltip("铰点修正：目标原点不在铰点上（如板中心绕边摆）时，填铰点相对目标原点的父空间偏移；零 = 绕目标自身原点摆")]
    private Vector3 pivotOffset;

    [Header("摆动参数")]
    [SerializeField, Tooltip("摆到位的角度（相对场景初始位，正负 = 摆向相反）。0 = 未配置，交互时报错拒绝")]
    private float swingAngle = 90f;

    [SerializeField, Min(0.1f), Tooltip("摆动角速度（度/秒）")]
    private float swingSpeed = 90f;

    [Header("锁定（可选）")]
    [SerializeField, Tooltip("被这个机关锁住的物体（Rigidbody）：场景里起步设 Is Kinematic = 固定、不受力；机关首次摆到位瞬间自动释放（取消 Kinematic）并永不重锁。留空 = 纯摆动机关")]
    private Rigidbody lockedRigidbody;

    [Header("测试")]
    [SerializeField, Tooltip("测试开关：Play 模式下勾选 = 开启机关（单向，开过即退役，取消勾选不关）。不是真实信号输入")]
    private bool open;

    // --- 开启状态（存活于协程之间） ---
    private float currentAngle;      // 当前相对基线的转角（0 = 初始位，swingAngle = 开到位）
    private bool opened;             // 已开启（单向：置位后永不复位）

    // --- 基线（首次使用捕获，目标可在场景里自由摆位） ---
    private bool hasBaseline;
    private Quaternion initialRotation;
    private Vector3 initialPosition;

    private bool lastOpen;           // 测试开关上次驱动的值
    private bool warnedMissingTarget;

    private void Update()
    {
        // 测试专用：Inspector 勾选"开"的上升沿驱动开启；开过即退役（组件自禁），
        // 此 Update 随之停摆 —— 已开的机关不再被测试勾选驱动。开启不可逆，取消勾选不关。
        if (open != lastOpen)
        {
            lastOpen = open;
            if (open) TryOpen();
        }
    }

    /// <summary>交互框架入口：开启机关（一次性；已开启再交互 = 无动作）。</summary>
    public void OnInteract() => TryOpen();

    /// <summary>机关是否已开启（存档读取）。</summary>
    public bool IsOpened => opened;

    /// <summary>
    /// 开启机关：摆向目标位并永久保持。幂等 —— 已开启直接返回（单向机关开不了第二次）。
    /// 目标未配置 / 摆角未配置 → 日志报错并拒绝（不静默）。
    /// </summary>
    public void TryOpen()
    {
        if (opened) return;   // 已开启：无动作（与 SlidingDoor"已开再开 = no-op"一致）
        if (target == null)
        {
            if (!warnedMissingTarget)
            {
                warnedMissingTarget = true;
                Debug.LogWarning($"[SwingSwitch] {name}: 没有配置 Target —— 交互被忽略。请把要摆动的物体拖到 Inspector", this);
            }
            return;
        }
        if (swingAngle == 0f)
        {
            Debug.LogWarning($"[SwingSwitch] {name}: Swing Angle 为 0，无摆动可做 —— 交互被忽略。请配置摆角（带符号 = 摆向）", this);
            return;
        }
        if (!hasBaseline) CaptureBaseline();

        opened = true;
        StartCoroutine(SwingTo());

        // 用后即退役：自禁组件 —— 瞄准系统把禁用的交互组件视为不可交互（见 BeeInteractionController），
        // 开过的机关从此无描边、按不动；协程不受 enabled 影响，照常摆完并在到位瞬间解锁。
        enabled = false;

        GameEvents.SwitchToggled?.Invoke(transform.position);   // 拨动瞬间音效(注册式同步)
    }

    // === 存档 (ISceneSaveable:机关 = 是否已开启 opened;恢复瞬间到位 + 同步解锁,不发音效) ===
    public string SaveableType => "SwingSwitch";

    public string CaptureToJson()
    {
        return JsonUtility.ToJson(new SwingState { swung = opened });
    }

    public void RestoreFromJson(string json)
    {
        var s = JsonUtility.FromJson<SwingState>(json);
        if (s == null)
        {
            Debug.LogWarning($"[SwingSwitch] {name}: 存档数据损坏，跳过机关状态恢复", this);
            return;
        }
        if (!s.swung) return;   // 未开：保持场景现状（组件启用，等待首次交互）

        // 已开：在全新场景实例上瞬间恢复到位并同步解锁（恢复先于任何游玩帧，不走动画、不发音效）
        enabled = true;         // 防御：若组件被存为禁用，恢复后保持逻辑一致（开启态无需再交互，仅解锁需要方法可达）
        opened = true;
        currentAngle = swingAngle;
        ApplyPose(swingAngle);
        UnlockOnce(true);
    }

    /// <summary>摆动协程：按角速度平滑逼近目标角，到位精确停止（协程结束，静止时零逐帧开销）。</summary>
    private IEnumerator SwingTo()
    {
        while (currentAngle != swingAngle)
        {
            // 目标随场景卸载被销毁（Unity 对象比较立即为 null）：直接退场，不再写已销毁的 Transform
            if (target == null)
                yield break;
            currentAngle = Mathf.MoveTowards(currentAngle, swingAngle, swingSpeed * Time.deltaTime);
            ApplyPose(currentAngle);
            yield return null;
        }
        // Snap to the exact endpoint（MoveTowards 已保证相等，这里显式收尾）
        ApplyPose(swingAngle);

        GameEvents.DoorClunk?.Invoke(target.position);   // 摆动到位碰撞音(借用门事件,注册式同步)
        UnlockOnce(false);                               // 摆到位瞬间解锁被锁物体
    }

    /// <summary>
    /// 释放被锁物体（只发生一次 —— 机关单向，摆到位只到一次）。lockedRigidbody 起步应是
    /// Is Kinematic（= 固定）；这里取消 Kinematic 交给物理。已经是动态 = 本来没锁住
    /// （配置问题已由 OnValidate 警告），不再空响解锁音。
    /// </summary>
    private void UnlockOnce(bool silent)
    {
        if (lockedRigidbody == null) return;
        if (!lockedRigidbody.isKinematic) return;   // 本就动态：没锁可解
        lockedRigidbody.isKinematic = false;

        if (!silent)
            GameEvents.DoorUnlock?.Invoke(lockedRigidbody.transform.position);   // 解锁音(借用门事件)
    }

    private void CaptureBaseline()
    {
        initialRotation = target.localRotation;
        initialPosition = target.localPosition;
        hasBaseline = true;
    }

    /// <summary>从基线重算摆姿：绕基线局部轴转 angle 度，可选绕铰点公转 —— 零漂移。</summary>
    private void ApplyPose(float angle)
    {
        if (!hasBaseline) CaptureBaseline();

        Quaternion q = Quaternion.AngleAxis(angle, initialRotation * AxisVector());
        target.localRotation = q * initialRotation;

        if (pivotOffset != Vector3.zero)
        {
            Vector3 pivot = initialPosition + pivotOffset;                     // 铰点（父空间，逐次用实时偏移重算）
            target.localPosition = pivot + q * (initialPosition - pivot);   // 绕铰点公转
        }
    }

    /// <summary>Rotation Axis 对应的局部轴向单位向量（目标自身局部坐标）。</summary>
    private Vector3 AxisVector()
    {
        switch (rotationAxis)
        {
            case RotAxis.X: return Vector3.right;
            case RotAxis.Y: return Vector3.up;
            default:        return Vector3.forward;
        }
    }

    /// <summary>
    /// 场景可视化（选中本组件时显示，纯编辑辅助）：
    /// 基线优先用已捕获值、未进 Play 用场景当前位姿 —— 与运行时基线同源，所见即所转。
    /// 青色箭头线 = 轴线（箭头 = 轴正向，正摆角按右手定则绕它旋转）；黄色十字 = 铰点（Pivot Offset 生效处）。
    /// </summary>
    private void OnDrawGizmosSelected()
    {
        if (target == null) return;

        Quaternion r0 = hasBaseline ? initialRotation : target.localRotation;
        Vector3 p0 = hasBaseline ? initialPosition : target.localPosition;

        // 局部 → 父空间 → 世界（父级不动，机制节点转动只发生在目标自身）
        Transform parent = target.parent;
        Vector3 dirParent = r0 * AxisVector();
        Vector3 pivotParent = p0 + pivotOffset;
        Vector3 dirWorld = parent != null ? parent.rotation * dirParent : dirParent;
        Vector3 pivotWorld = parent != null ? parent.TransformPoint(pivotParent) : pivotParent;

        // 与轴垂直的参考方向（画箭头头部 / 铰点十字用）
        Vector3 up = Mathf.Abs(dirWorld.y) < 0.99f ? Vector3.up : Vector3.right;
        Vector3 side = Vector3.Cross(dirWorld, up).normalized;
        Vector3 up2 = Vector3.Cross(dirWorld, side).normalized;

        float headLen = GizmoArrowLength * 0.18f;
        float headHalf = headLen * 0.6f;
        Vector3 tip = pivotWorld + dirWorld * GizmoArrowLength;
        Vector3 headBase = tip - dirWorld * headLen;

        // 轴线 + 箭头头部
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(pivotWorld, tip);
        Gizmos.DrawLine(tip, headBase + side * headHalf);
        Gizmos.DrawLine(tip, headBase - side * headHalf);

        // 铰点十字（位于垂直轴的小平面上，一眼看出旋转中心在目标身上的哪里）
        float s = headLen * 0.4f;
        Gizmos.color = Color.yellow;
        Gizmos.DrawLine(pivotWorld - side * s, pivotWorld + side * s);
        Gizmos.DrawLine(pivotWorld - up2 * s, pivotWorld + up2 * s);
    }

    // --- 场景可视化参数 ---
    private const float GizmoArrowLength = 1.2f;   // 轴线箭头长度（米，纯编辑辅助，视觉缩放不影响运行）

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (target == null)
        {
            Debug.LogWarning($"[SwingSwitch] {name}: Target 未配置 —— 交互会报错并忽略。请拖要摆动的物体（交互件即转动体时拖自己）", this);
            return;
        }
        if (swingAngle == 0f)
            Debug.LogWarning($"[SwingSwitch] {name}: Swing Angle 为 0 —— 交互会报错并忽略。请配置摆角", this);
        var rb = target.GetComponent<Rigidbody>();
        if (rb != null && !rb.isKinematic)
            Debug.LogWarning($"[SwingSwitch] {name}: 目标带动态 Rigidbody，Transform 驱动会与物理冲突 —— 请改为 Is Kinematic 或去掉刚体", this);
        if (lockedRigidbody != null)
        {
            if (!lockedRigidbody.isKinematic)
                Debug.LogWarning($"[SwingSwitch] {name}: 被锁物体 {lockedRigidbody.name} 不是 Is Kinematic —— “固定”不成立（本来就受物理可动）。请勾上它的 Is Kinematic，机关开启后会自动释放", this);
            if (lockedRigidbody.transform == target)
                Debug.LogWarning($"[SwingSwitch] {name}: Locked Rigidbody 就是摆动 Target —— 同一物体既摆又解锁没有意义。请填另一个物体", this);
        }
    }
#endif
}
