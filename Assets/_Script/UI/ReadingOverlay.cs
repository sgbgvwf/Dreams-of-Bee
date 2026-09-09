using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// 全屏阅读界面（纸张 / 书本 / 海报的内容观察，一次只能读一份）。本组件只管三件事：
///  ① 根（root）的开关 —— 半黑背景 / 提示文字等视觉都是 root 的静态子物体，场景里配好，
///     整块随 root 一起显示/隐藏，脚本不单独持有它们的引用、也不去改它们的颜色文字；
///  ② 把观察物（PaperReading）的内容图 + 展示规格（displaySize，宽×高画布单位）铺到 Content 上；
///  ③ 会话期冻结玩家、右键退出（见下）。
/// 视觉尺寸完全由观察物自己定，本组件不做任何按屏幕的自动缩放；背景半黑程度、提示文字
/// 在场景里一次配死（如 Backdrop Image 填 RGBA(0,0,0,0.66)、底部一行 TMP）。
///
/// 场景化接线（与 FadeOverlay / PauseMenu 同款，视觉在场景里摆、脚本只留行为）：
///   挂 Persistance 场景 Pause Canvas(sorting 10)下的常驻 "Reading Overlay" GO（保持 active），
///   把 UI 引用拖齐即可 —— 输入动作经 GameInput 统一取，无需拖资产。
/// 渲染层约定 —— 阅读中按 Esc 要"直接进暂停页、阅读保持打开"：
///   root 是 Pause Canvas 的兄弟节点，层级排在暂停菜单根之前（同 Canvas 内后绘制者在上）：
///   天然盖过 HUD / 房间 UI（该 Canvas 整体已验证在 HUD 之上）、被暂停菜单盖住、被 Fade 黑幕(100)盖住。
/// 需要场景手动摆的结构：root = 整块阅读界面（默认 inactive，开/关 = SetActive）；其下
///   Backdrop Image（anchors 拉伸填满 root，静态半黑颜色）、Content RawImage（anchors / pivot
///   居中中心 —— 脚本按物品规格直接设 sizeDelta，非居中锚点会跑位）、底部 Hint 文字（可留空）。
///
/// 会话语义：
///   - 打开 = 冻结玩家（BeeFlightController.InputLocked = true，与 DrownReturn 救援同一接管机制，
///     只锁输入不冻结世界），内容铺上屏；关闭 = 释放。物品 3D 位置从始至终不动，纯 UI 模拟阅读。
///   - Esc 不归本组件管：PauseInput → PauseMenu 原样暂停（timeScale=0 + 菜单盖在阅读层上），
///     期间无人动 InputLocked → Resume 后原样回到阅读。本组件的右键退出监听在暂停时忽略按键，
///     防在菜单里误关阅读。
///   - 自动关闭的唯一路径 = 流程离开游玩（FlowStateSO 进主菜单 / 结局）：界面不残留、锁必释放。
///     本组件订阅事件管线 GameEvents.FlowStateChanged 感知迁移即关（不逐帧轮询镜像）。
///
/// 与交互框架的竞态约定（打开/退出都走同一个右键，谁收这一帧的按键必须唯一）：
///   - 打开帧：BeeInteractionController.Update 分发 OnInteract → OpenReading（此刻订阅 performed）。
///     performed 在输入处理阶段先于 Update 触发，故打开帧这一下不会命中刚订阅的退出监听。
///   - 退出按下帧：performed 处理先关会话；BeeInteractionController 也监视 InputLocked
///     （见 BeeInteractionController 的改动注释），所以整帧都不分发 —— 该右键不会重开。
///     为让"关界面那一帧"依然全程抑制，按键路径的解锁延后到本帧末（unlockPending，LateUpdate 清），
///     同帧另设 reopenGuard 拒绝任何再打开（防未来其它路径同帧重开）。
///   - 非按键路径的关闭（流程离开游玩，经 FlowStateChanged 处理器发现）直接解锁即可：流程已释放光标、
///     交互控制器天然不活跃，无重开风险。
///   - 打开期间每帧重上锁（幂等）：若阅读中途被 DrownReturn 救援收尾把 InputLocked 复位，
///     下一帧立刻补回，不存在"阅读开着却可操作"的窗口。
/// </summary>
public class ReadingOverlay : MonoBehaviour
{
    public static ReadingOverlay Instance { get; private set; }

    /// <summary>阅读会话是否打开（打开期间玩家输入被接管）。</summary>
    public bool IsOpen { get; private set; }

    // === UI 引用（Pause Canvas 场景化摆放，见类注释） ===
    [SerializeField, Tooltip("整块阅读界面根（含半黑背景/内容/提示等全部静态视觉，场景里默认 inactive，开/关 = SetActive）。")]
    private GameObject root;
    [SerializeField, Tooltip("内容 RawImage（anchors / pivot 居中中心）：每次打开把贴图与尺寸（观察物 displaySize 规格）设上去。")]
    private RawImage contentImage;

    // --- 运行期 ---
    private InputAction interactAction;    // 右键退出监听（仅会话打开期间订阅）；经 GameInput 取，与 BeeInteractionController 共用 Interact
    private BeeFlightController flight;    // 打开时按 tag 惰性解析的玩家控制器（冻结/解锁；直玩无玩家场景 = null）
    private bool unlockPending;            // 按键关闭：解锁延后到本帧末（LateUpdate 清），见类注释竞态约定
    private bool reopenGuard;              // 按键关闭帧拒绝任何再打开（同 LateUpdate 清）

    // --- 配置错误提示（只报一次） ---
    private bool warnedBadSetup;
    private bool warnedMissingAction;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        interactAction = GameInput.Interact;
        // 资产缺失 / 缺 Interact:interactAction 为 null(GameInput 已报错),首次打开时报错并拒绝开(打开就关不掉 = 软锁)
    }

    private void OnEnable()
    {
        // 事件管线约定:静态委托跨场景持久 —— 实例方法订阅,OnDisable 成对退订
        GameEvents.FlowStateChanged += OnFlowStateChanged;
    }

    private void OnDisable()
    {
        GameEvents.FlowStateChanged -= OnFlowStateChanged;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (interactAction != null)
            interactAction.performed -= OnInteractPressed;   // 动作常驻启用,卸载只需退订
        if (IsOpen && flight != null) flight.InputLocked = false;   // 异常卸载兜底：别把玩家锁死
    }

    // ==================== 会话入口 ====================

    /// <summary>
    /// 打开一次阅读会话（由 PaperReading.OnInteract 在交互分发中调用）：冻结玩家、铺内容、
    /// 挂右键退出监听。已在会话中 / 刚按键关闭的那一帧 → 拒绝（一次只读一份，见类注释竞态约定）。
    /// </summary>
    public void OpenReading(PaperReading source)
    {
        if (source == null || source.PageImage == null) return;   // 物品侧已警告过配置错误
        if (IsOpen || reopenGuard) return;
        if (root == null || contentImage == null)
        {
            if (!warnedBadSetup)
            {
                warnedBadSetup = true;
                Debug.LogError($"[ReadingOverlay] {name}: UI 引用没接齐（root / contentImage）—— 阅读界面开不了。请按类注释在 Pause Canvas 下摆放并拖引用", this);
            }
            return;
        }
        if (interactAction == null)
        {
            if (!warnedMissingAction)
            {
                warnedMissingAction = true;
                Debug.LogError($"[ReadingOverlay] {name}: 没有可用的 Interact 右键动作(GameInput 未提供)—— 打开后无法右键退出(软锁),拒绝打开。请检查输入资产是否含名为 Interact 的动作", this);
            }
            return;
        }

        // --- 冻结玩家（动玩家动作：惰性按 tag 解析，仿 DrownReturn） ---
        flight = ResolvePlayerFlight();
        if (flight != null) flight.InputLocked = true;

        // --- 铺内容：贴图 + 尺寸 = 观察物上配的展示规格（displaySize），不做屏幕自动缩放 ---
        contentImage.texture = source.PageImage;
        contentImage.rectTransform.sizeDelta = source.DisplaySize;
        root.SetActive(true);   // 背景/提示等静态子物体随根一起亮
        IsOpen = true;

        // 退出监听：订阅发生在打开这一帧的 Update 阶段 —— 打开帧的右键早已 performed 完，
        // 不会命中本次订阅；此后每一下右键都由这里收（BeeInteractionController 已被 InputLocked 抑制整帧）。
        // 动作常驻启用(GameInput 统一管理),会话期只订阅/退订,不再 Enable/Disable。
        interactAction.performed += OnInteractPressed;

        GameEvents.PaperOpen?.Invoke();
    }

    /// <summary>
    /// 关闭阅读会话（右键退出 / 流程离开游玩时调用）。inputClose = 由右键触发：
    /// 解锁延后到本帧末并拒绝同帧再打开（见类注释竞态约定）；非按键路径（流程层已释放光标）直接解锁。
    /// </summary>
    private void CloseReading(bool inputClose)
    {
        if (!IsOpen) return;
        IsOpen = false;
        if (root != null) root.SetActive(false);

        if (interactAction != null)
            interactAction.performed -= OnInteractPressed;

        if (inputClose)
        {
            unlockPending = true;   // 关界面那一帧保持整帧上锁：BeeInteractionController 全程不分发
            reopenGuard = true;     // 同上，该帧拒绝任何再打开
        }
        else if (flight != null)
        {
            flight.InputLocked = false;   // 流程离开游玩：光标已由流程释放，交互控制器天然不活跃
        }

        GameEvents.PaperClose?.Invoke();
    }

    // ==================== 输入 / 状态维护 ====================

    /// <summary>
    /// 流程状态迁移 → 离开游玩自动关阅读（替换原逐帧轮询 FlowStateSO）。同步触发于
    /// GameFlowManager.SetState 内部 —— 此时镜像已写入新状态，读 PauseAllowed 与旧轮询判定同值；
    /// 暂停不改 FlowState，不触发，故"暂停不在此列"语义由事件自身保证。
    /// </summary>
    private void OnFlowStateChanged(FlowState prev, FlowState next)
    {
        if (!IsOpen) return;
        var so = FlowStateSO.Instance;
        if (so != null && !so.PauseAllowed)
            CloseReading(inputClose: false);
    }

    /// <summary>右键退出入口（仅会话打开期间被订阅）。暂停菜单打开时忽略 —— 别让菜单里的右键误关阅读。</summary>
    private void OnInteractPressed(InputAction.CallbackContext context)
    {
        if (PauseMenu.IsPaused) return;
        CloseReading(inputClose: true);
    }

    private void Update()
    {
        if (!IsOpen) return;

        // 会话期间保持上锁（幂等）：若阅读中途被 DrownReturn 救援收尾把 InputLocked 复位，下一帧补回，
        // 不存在"阅读开着却可操作"的窗口。
        if (flight != null && !flight.InputLocked)
            flight.InputLocked = true;
    }

    private void LateUpdate()
    {
        // 按键关闭的解锁统一在本帧末：整帧 InputLocked 抑制交互控制器后，再放行
        // （同一帧里 Update 已经全部跑完，不存在同帧按键重开）。
        if (!unlockPending) return;
        unlockPending = false;
        reopenGuard = false;
        if (flight != null) flight.InputLocked = false;
    }

    /// <summary>冻结对象 = 玩家控制器（对玩家做动作的常驻侧组件统一按 tag 惰性解析，仿 DrownReturn.ResolvePlayerFlight）。</summary>
    private static BeeFlightController ResolvePlayerFlight()
    {
        var go = GameObject.FindGameObjectWithTag("Player");
        return go != null ? go.GetComponent<BeeFlightController>() : null;
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (root == null || contentImage == null)
            Debug.LogWarning($"[ReadingOverlay] {name}: UI 引用没接齐 —— 需要 root（整块阅读界面，含背景/内容/提示，默认 inactive）与 contentImage（居中 RawImage）。见类注释的场景化接线", this);
        if (contentImage != null)
        {
            var rt = contentImage.rectTransform;
            Vector2 center = new Vector2(0.5f, 0.5f);
            if (rt.anchorMin != center || rt.anchorMax != center)
                Debug.LogWarning($"[ReadingOverlay] {name}: Content 的 anchors 不是居中中心 —— 打开时按物品规格直接设 sizeDelta，非居中锚点会跑位/错位。请把 anchors 设成 0.5, 0.5", this);
            if (rt.pivot != center)
                Debug.LogWarning($"[ReadingOverlay] {name}: Content 的 pivot 不是 0.5, 0.5 —— 尺寸围绕 pivot 展开，请把 pivot 设成 0.5, 0.5 否则内容不居中", this);
        }
    }
#endif
}
