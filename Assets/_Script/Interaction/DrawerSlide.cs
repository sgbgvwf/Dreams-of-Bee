using UnityEngine;
using System.Collections;

/// <summary>
/// 抽屉（可交互功能脚本，IInteractable 接入交互框架）：瞄准抽屉按下交互键（右键）
/// → 抽屉开/关来回切换。中途再按立即反向，滑行过程流畅掉头。
///
/// 挂法：一个抽屉一个组件 —— 挂在抽屉的滑出节点上（抽屉自身要有 Collider 供瞄准，
/// 交互框架只负责射线判定与描边，本脚本只提供行为），Target 留空 = 滑组件所在节点；
/// 也可以挂在柜体等静止物体上、Target 另拖要滑出的抽屉节点 —— Target 是独立引用，
/// 两种挂法同样成立。柜体本身（不滑的部分）不要共挂本组件。
///
/// 滑移：抽屉沿 slideDirection（抽屉自身局部方向）滑出 slideDistance 距离。关闭位
/// 在首次使用时捕获（= 场景摆好的位姿），开启/关闭全程零漂移；滑动只平移不旋转，
/// 方向在基线时换算成世界方向一次即可，不会漂移。slideCurve 映射滑动进度(0..1)
/// 到位移(0..1)：直线 = 匀速，S 曲线(默认) = 平滑加减速。
///
/// 抽屉始终可交互（开着的也能按回来）；无锁定概念。注意：抽屉不锁玩家 ——
/// 玩家正挡着时滑出会穿模（Transform 驱动，与 SlidingDoor 同款行为，不做碰撞检测）。
///
/// 音效：切换瞬间 DoorOpen / DoorClose（借用门事件，抽屉方向=开/关语义），
/// 滑动到位 DoorClunk —— 全部注册式同步。存档 = 抽屉开闭态（open 单向目标态，
/// 中途滑动不存进度，与 SlidingDoor 同款：恢复 = 从关闭位重新滑向目标态）。
/// 场景可视化：选中本组件时 Scene 视图在滑出路径上画青色箭头 —— 起点 = 关闭位
/// （运行时用基线、未进 Play 用当前位姿），箭头 = 滑出方向，箭头尖端 = 开到位。
/// 对着箭头调 Inspector 里的方向与距离即可。
/// </summary>
public class DrawerSlide : MonoBehaviour, IInteractable, ISceneSaveable
{
    [SerializeField, Tooltip("要滑出的抽屉节点（本身要带 Collider 供瞄准）。留空 = 滑组件所在节点（挂在抽屉上时即此用法）")]
    private Transform target;

    [Header("滑动参数")]
    [SerializeField, Tooltip("滑出方向（抽屉自身局部空间，相对它当前的姿态）。配反了抽屉往里缩 = 取负号方向；对着 Scene 箭头调")]
    private Vector3 slideDirection = Vector3.forward;

    [SerializeField, Tooltip("滑出距离（世界单位）。开位 = 关闭位 + 滑出方向 × 距离，对着箭头调到抽屉实际深度")]
    [Min(0.01f)]
    private float slideDistance = 0.45f;

    [SerializeField, Tooltip("一次完整开/关所用秒数")]
    [Min(0.01f)]
    private float duration = 0.5f;

    [SerializeField, Tooltip("滑动进度(0..1)到位移(0..1)的映射。直线 = 匀速，S 曲线 = 平滑加减速")]
    private AnimationCurve slideCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Header("测试")]
    [SerializeField, Tooltip("测试开关：Play 模式下勾选 = 滑出、取消 = 收回（带动真实滑动，不是瞬移）。不是真实信号输入")]
    private bool open;

    // --- 滑出目标（Awake 解析一次：Target 留空 = 组件自身；运行时改动不生效） ---
    private Transform slideTarget;

    private void Awake()
    {
        slideTarget = target != null ? target : transform;
    }

    // --- 滑移状态 ---
    private float progress;       // 0 = 关，1 = 开；存活于协程之间
    private bool targetOpen;      // 当前去向：true = 滑向开位
    private Coroutine slideRoutine;
    private bool hasBaseline;
    private Vector3 closedPosition;   // 关闭位（首次使用捕获 = 场景摆好的位姿）
    private Vector3 openDirection;    // 滑出世界方向（基线时换算一次，滑动不旋转所以恒不变）

    private bool lastOpen;            // 测试开关上次驱动的值
    private bool warnedBrokenSlide;

    private void Update()
    {
        // 测试专用：Inspector 勾选"开"的升降沿驱动滑移；真实交互（OnInteract）不受影响。
        if (open != lastOpen)
        {
            lastOpen = open;
            SignalTo(open);
        }
    }

    /// <summary>交互框架入口：抽屉开/关来回切换（滑行中途按下立即反向）。</summary>
    public void OnInteract()
    {
        SignalTo(!targetOpen);
    }

    /// <summary>抽屉当前去向（true = 正在滑向开位或已开到位）。</summary>
    public bool IsOpen => targetOpen;

    /// <summary>开抽屉。已在开或正在开 = 无动作。</summary>
    public void OpenDrawer()
    {
        SignalTo(true);
    }

    /// <summary>关抽屉。已在关或正在关 = 无动作。</summary>
    public void CloseDrawer()
    {
        SignalTo(false);
    }

    // === 存档 (ISceneSaveable:抽屉 = 开闭目标态;恢复走"设目标态"入口,从场景摆好的关闭位重新滑) ===
    public string SaveableType => "DrawerSlide";

    public string CaptureToJson()
    {
        return JsonUtility.ToJson(new DrawerState { open = targetOpen });
    }

    public void RestoreFromJson(string json)
    {
        var s = JsonUtility.FromJson<DrawerState>(json);
        if (s == null)
        {
            Debug.LogWarning($"[DrawerSlide] {name}: 存档数据损坏，跳过抽屉状态恢复", this);
            return;
        }
        SignalTo(s.open);
    }

    /// <summary>
    /// 滑向目标态的总入口（测试开关 / 交互 / 存档恢复全部汇入此处）：
    /// 已在去向目标 = no-op；首次使用时捕获基线（关闭位 + 世界滑出方向）。
    /// 守卫都过了才响切换音 —— 不空响。
    /// </summary>
    private void SignalTo(bool opening)
    {
        if (slideTarget == null)
        {
            if (!warnedBrokenSlide)
            {
                warnedBrokenSlide = true;
                Debug.LogWarning($"[DrawerSlide] {name}: 找不到滑出目标 —— 交互被忽略。请把本组件挂到抽屉节点上，或把抽屉节点拖到 Target", this);
            }
            return;
        }
        if (!hasBaseline) CaptureBaseline();

        if (opening == targetOpen) return;   // already headed there: no-op
        targetOpen = opening;
        StartSlide();

        // 注册式音效同步(借用门事件，与 SwingSwitch 同款做法)：抽屉开始滑动的瞬间广播
        if (opening) GameEvents.DoorOpen?.Invoke(slideTarget.position);
        else GameEvents.DoorClose?.Invoke(slideTarget.position);
    }

    /// <summary>切换开关（Play 模式下 Inspector 右键测试用）。</summary>
    [ContextMenu("Toggle Drawer")]
    private void ToggleDrawerFromInspector()
    {
        OnInteract();
    }

    /// <summary>
    /// 启动滑移协程：滑行中途的新信号取消旧协程、从当前进度继续 ——
    /// 掉头时方向平滑反转、无跳变。
    /// </summary>
    private void StartSlide()
    {
        if (slideRoutine != null) StopCoroutine(slideRoutine);
        slideRoutine = StartCoroutine(SlideTo(targetOpen));
    }

    private IEnumerator SlideTo(bool opening)
    {
        float target = opening ? 1f : 0f;
        while (progress != target)
        {
            // 目标随场景卸载被销毁（Unity 对象比较立即为 null）：直接退场，不再写已销毁的 Transform
            if (slideTarget == null)
                yield break;
            progress = Mathf.MoveTowards(progress, target, Time.deltaTime / duration);
            ApplyPose();
            yield return null;
        }
        // Snap to the exact endpoint（MoveTowards 已保证相等，这里显式收尾）；协程结束，静止时零逐帧开销
        ApplyPose();
        slideRoutine = null;

        GameEvents.DoorClunk?.Invoke(slideTarget.position);   // 滑到位碰撞音(借用门事件,注册式同步)
    }

    private void CaptureBaseline()
    {
        closedPosition = slideTarget.position;
        // slideDirection 是抽屉自身的局部方向：斜着摆放的柜子也沿自己轴向滑。
        // 滑行全程只平移不旋转，世界方向换算一次即可，不会漂移。
        openDirection = slideTarget.TransformDirection(slideDirection).normalized;
        hasBaseline = true;
    }

    private void ApplyPose()
    {
        slideTarget.position = Vector3.Lerp(closedPosition,
            closedPosition + openDirection * slideDistance, slideCurve.Evaluate(progress));
    }

    /// <summary>
    /// 场景可视化（选中本组件时显示，纯编辑辅助）：
    /// 基线优先用已捕获值、未进 Play 用场景当前位姿 —— 与运行时基线同源，所见即所滑。
    /// 青色箭头线 = 滑出路径：起点 = 关闭位，箭头 = 滑出方向，箭头尖端 = 开到位位置。
    /// 对着箭头调 slideDirection / slideDistance —— 方向反了改负号，距离看尖端是否对准开到位。
    /// </summary>
    private void OnDrawGizmosSelected()
    {
        // 编辑模式（未进 Play）没有运行时缓存，这里现场解析一次，与 Awake 的解析同源
        Transform t = target != null ? target : transform;
        if (t == null) return;

        Vector3 basePos = hasBaseline ? closedPosition : t.position;
        // 未进 Play 时没有基线：用场景当前姿态算方向（滑动只平移，姿态即关闭位姿态）
        Vector3 dir = hasBaseline ? openDirection : t.TransformDirection(slideDirection).normalized;
        float len = Mathf.Max(0.05f, slideDistance);

        float headLen = Mathf.Min(0.18f, len * 0.22f);   // 箭头头部尺寸跟距离走，距离极小也不炸
        float headHalf = headLen * 0.55f;
        Vector3 tip = basePos + dir * len;
        Vector3 headBase = tip - dir * headLen;

        Vector3 up = Mathf.Abs(dir.y) < 0.99f ? Vector3.up : Vector3.right;
        Vector3 side = Vector3.Cross(dir, up).normalized;

        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(basePos, tip);
        Gizmos.DrawLine(tip, headBase + side * headHalf);
        Gizmos.DrawLine(tip, headBase - side * headHalf);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (slideDirection == Vector3.zero)
            Debug.LogWarning($"[DrawerSlide] {name}: Slide Direction 为 0 —— 抽屉不会动。请配抽屉自身的滑出方向", this);
        if (slideDistance <= 0f)
            Debug.LogWarning($"[DrawerSlide] {name}: Slide Distance 为 0 —— 抽屉不会动。请配滑出距离", this);
        if (GetComponentInParent<Interactable>() != null)
            Debug.LogWarning($"[DrawerSlide] {name}: 与 Interactable（可拾取物）挂在一起 —— 拾取流程会优先接管，交互按不出抽屉。请去掉其一", this);
        var other = GetComponentInParent<IInteractable>();
        if (other != null && !ReferenceEquals(other, this))
            Debug.LogWarning($"[DrawerSlide] {name}: 与另一个 IInteractable（按式交互物）在同一条瞄准链上 —— 按一次分发给谁不确定。滑出抽屉应自成一体，不共链", this);
    }
#endif
}
