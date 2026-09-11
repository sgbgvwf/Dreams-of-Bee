using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering.HighDefinition;

/// <summary>游戏流程状态(全局静态查询口;内容代码与 UI 据此门控自己的行为)。</summary>
public enum FlowState
{
    /// <summary>启动瞬间(未决策前)。</summary>
    Boot,
    /// <summary>主菜单(Room_00 背景;无玩家场景)。</summary>
    MainMenu,
    /// <summary>游玩中(玩家场景 + 关卡已加载)。</summary>
    InGame,
    /// <summary>结局演出(默认纯色幕;结局定义可选专属演出场景)。</summary>
    Ending,
    /// <summary>开发者直玩(未从 Persistance 启动、无 LevelTransitionManager):流程惰性,保持旧手感。</summary>
    DevDirectPlay,
}

/// <summary>
/// 游戏流程管理器(单例,BeforeSceneLoad 自建 + DontDestroyOnLoad,仿 AudioManager 模式):
/// 流程状态机 + 场景编排的唯一入口 —— 主菜单 / 新游戏 / 继续(读档) / 返回主菜单 / 结局,
/// 驱动 LevelTransitionManager(开局 / 读档 / 收局),并在关键节点(开局到达 / 每关到达站稳 /
/// 返回主菜单前)往活动档自动存档。
///
/// 场景布局:
///   - 主菜单:Persistance 常驻 + 背景场景(Room_00 —— 按文件名从 Build Settings 解析,
///     只做主菜单背景)附加加载,配"Menu Camera"(场景无相机时运行时自建);
///   - 结局:默认不加载任何场景,EndingOverlay 自带纯色幕演出;结局定义可显式指定
///     backdropScenePath(专属演出场景,需作者自配相机),此时才加载它;
///   - 游玩:卸载背景 → LevelTransitionManager 加载玩家场景 + 关卡;
///   - Room_00 永不进关卡列表(见 LevelTransitionManager 的列表兜底排除),也只做主菜单背景,
///     不会作为结局演出地。
///
/// 多周目:meta.completedRuns 只在结局确认后 +1;新开局周目号 = completedRuns + 1,
/// 内容按周目分支直接读 SaveSystem.CurrentPlaythroughNumber / 全局 KV / 结局解锁。
///
/// 开发者直玩:未从 Persistance 启动(无 LevelTransitionManager)→ State = DevDirectPlay,
/// 不建菜单 / 结局 UI、不装背景 —— 直接 Play 任意房间的开发体验与改前一致。
/// </summary>
public class GameFlowManager : MonoBehaviour
{
    public static GameFlowManager Instance { get; private set; }

    // === 背景场景 / 菜单相机 ===
    private string currentBackdropPath = "";   // 当前加载的背景场景路径(空 = 无)
    private GameObject menuCamera;             // 菜单相机(自建或场景里作者摆的)
    private bool cameraOwned;                  // 相机是否为本流程自建(随场景卸载销毁后要清引用)
    private bool warnedMissingBackdrop;

    // === 流程状态 ===
    private FlowState state = FlowState.Boot;
    private bool autosaveQueued;               // settle 自动存档协程防重入

    // === 当前结局(确认离开时结算周目 / 结局入库) ===
    private string currentEndingId = "";
    private int endingRunPlaythrough = 1;

    // ==================== 对外静态 API(内容 / UI 调用) ====================

    public static FlowState State => Instance != null ? Instance.state : FlowState.Boot;

    /// <summary>当前是否在游玩中(内容脚本常据此门控:如只读查询 / 交互限定)。</summary>
    public static bool IsGameplayActive => State == FlowState.InGame;

    /// <summary>本局周目号(主菜单显示 / 内容差异分支用;无活动局 = 下一局周目)。</summary>
    public static int PlaythroughNumber => SaveSystem.CurrentPlaythroughNumber;

    /// <summary>开始新游戏(槽位选择 / 覆盖确认由菜单 UI 先完成;空 / 占用都走到这里即执行)。</summary>
    public static void StartNewGame(int slotIndex) => Instance?.RequestNewGame(slotIndex);

    /// <summary>继续上次游玩的存档(标题「继续游戏」按钮;已通关 / 无上一次时按钮应不可点,见 CanContinueLastGame)。</summary>
    public static void ContinueLastGame() => Instance?.RequestContinueLast();

    /// <summary>「上一次游戏」包装:游中 = 当前活动槽;主菜单 = 记忆的最后游玩槽(meta.lastSlotIndex)。</summary>
    public static int LastGameSlotIndex =>
        SaveSystem.HasActiveRun ? SaveSystem.ActiveSlotIndex : SaveSystem.Meta.lastSlotIndex;

    /// <summary>标题「继续游戏」是否可点:存在「上一次游戏」且该档仍有可玩内容(未通关 / 非空)。
    /// 已通关或无上一次 → false,UI 据此置灰;逻辑侧读档同样只接受这种状态。</summary>
    public static bool CanContinueLastGame
    {
        get
        {
            int slot = LastGameSlotIndex;
            if (slot < 0 || slot >= SaveSchema.SlotCount) return false;
            return SaveSystem.DescribeSlot(slot).Kind == SlotKind.InProgress;
        }
    }

    /// <summary>读指定槽的档继续(存档系统页按槽读档;空槽请走 StartNewGame)。</summary>
    public static void ContinueGameAtSlot(int slotIndex) => Instance?.RequestContinueSlot(slotIndex);

    /// <summary>触发结局(内容代码任意时刻调用;结局门经 LevelTransitionManager.EndingRequested 走同一入口)。</summary>
    public static void TriggerEnding(string endingId) => Instance?.RequestEnding(endingId);

    /// <summary>返回主菜单(暂停菜单按钮;含存档 + 收局 + 换背景)。</summary>
    public static void QuitToMenu() => Instance?.RequestQuitToMenu();

    /// <summary>结局幕的"返回主菜单"按下:结算周目 / 结局入库 / 槽位置已通关,然后回主菜单。</summary>
    public static void EndingConfirmed() => Instance?.ConfirmEnding();

    // ==================== 生命周期 ====================

    /// <summary>在任何场景加载前自建常驻流程管理器(仿 AudioManager 模式)。</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void AutoCreate()
    {
        // 防重复:Enter Play Mode Options 关闭 Domain Reload 时静态不重置 → 查残留对象
        if (FindObjectOfType<GameFlowManager>() != null) return;
        var go = new GameObject("[GameFlow]");
        DontDestroyOnLoad(go);
        go.AddComponent<GameFlowManager>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        // 淡幕 / 菜单 / 结局 UI 均为场景资产(Persistance 的 Fade/Pause/Ending Canvas、Room_00 的主菜单)——
        // 背景场景(Room_00)由本流程附加加载,那些 Canvas 与流程不在同一个加载时序里,这里不再自建任何 UI。
    }

    private void Start()
    {
        // 决策:无关卡过渡管理器(未从 Persistance 启动)→ 开发者直玩,流程惰性
        var ltm = LevelTransitionManager.Instance;
        if (ltm == null)
        {
            SetState(FlowState.DevDirectPlay);
            Debug.Log("[GameFlow] 未找到 LevelTransitionManager(直接 Play 了房间场景?)→ 开发者直玩模式,流程惰性");
            return;
        }

        // 订阅关卡管理器的稳定点与结局门
        ltm.Settled += OnLtmSettled;
        ltm.EndingRequested += OnEndingDoorRequested;

        // 从 Persistance 启动 → 主菜单:全黑开局,背景就绪后揭开
        SetState(FlowState.MainMenu);
        FadeOverlay.Instance?.SetAlpha(1f);
        StartCoroutine(MenuBootRoutine());
    }

    private IEnumerator MenuBootRoutine()
    {
        yield return ShowBackdropRoutine(ResolveDefaultBackdropPath());
        // 主菜单 UI 在 Room_00 背景场景里,加载完即自驱亮起(轮询 FlowStateSO:MainMenu 才显示),流程不调用
        ReleaseCursor();
        yield return FadeOverlay.FadeRoutine(0f, 0.5f);
    }

    // ==================== 状态切换 ====================

    private void SetState(FlowState next)
    {
        if (state == next) return;
        FlowState prev = state;
        state = next;

        // 运行状态镜像:UI 组件轮询 FlowStateSO 自驱显隐(主菜单 UI 只在 MainMenu 状态亮),
        // 流程不再逐个 Show/Hide 界面 —— 只负责把状态写对。
        FlowStateSO.Instance?.Write(state);

        // 游玩 HUD 的显隐由场景门控天然完成:体力条在 Player 场景(只在游玩加载),菜单 / 结局时玩家场景已卸载。
        // 通用事件管线通知(先写镜像后 raise,处理器读镜像与比对载荷等价,见 Core/GameEvents.cs)。
        GameEvents.FlowStateChanged?.Invoke(prev, next);
    }

    private void Update()
    {
        // 游玩时长累计:只在正式流程的游玩状态计时;暂停(timeScale = 0 → deltaTime = 0)自然不计
        if (state == FlowState.InGame)
            SaveSystem.TickSessionClock(Time.deltaTime);
    }

    /// <summary>菜单 / 结局需要自由光标(点击按钮);游玩时光标由 BeeFlightController 自己锁。</summary>
    private static void ReleaseCursor()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    /// <summary>开发者直玩时主菜单操作会被状态门拒绝 —— 日志补一句指引,免得误以为功能坏了。</summary>
    private static string DevDirectHint()
    {
        return State == FlowState.DevDirectPlay
            ? "（开发者直玩：请从 Persistance 场景启动以使用主菜单功能）"
            : "";
    }

    // ==================== 请求入口(静态 API → 实例,含状态门) ====================

    private void RequestNewGame(int slotIndex)
    {
        if (State != FlowState.MainMenu)
        {
            Debug.LogWarning($"[GameFlow] 新游戏请求被拒：当前不在主菜单（State = {State}）{DevDirectHint()}");
            return;
        }
        StartCoroutine(NewGameRoutine(slotIndex));
    }

    private void RequestContinueLast()
    {
        if (State != FlowState.MainMenu)
        {
            Debug.LogWarning($"[GameFlow] 继续游戏请求被拒：当前不在主菜单（State = {State}）{DevDirectHint()}");
            return;
        }
        // 标题「继续游戏」= 续「上一次游戏」:只对仍可玩的档生效(未通关 / 非空)。
        // 已通关 / 无上一次 → 按钮应不可点(UI 用 CanContinueLastGame 置灰),这里防御性拒绝;
        // 已通关档要开新周目请走存档页的「用新游戏覆盖」(槽页的「继续」内部自行转新周目,与此不同)。
        if (!CanContinueLastGame)
        {
            Debug.Log("[GameFlow] 上一次游戏不可继续（无记录或已通关），停留主菜单");
            return;
        }
        StartCoroutine(ResumeRoutine(SaveSystem.Meta.lastSlotIndex));
    }

    /// <summary>读指定槽的档继续(存档系统页的槽按钮用:空槽由 UI 层走 StartNewGame)。</summary>
    public void RequestContinueSlot(int slotIndex)
    {
        if (State != FlowState.MainMenu)
        {
            Debug.LogWarning($"[GameFlow] 读档请求被拒：当前不在主菜单（State = {State}）{DevDirectHint()}");
            return;
        }
        if (slotIndex < 0 || !SaveSystem.SlotExists(slotIndex))
        {
            Debug.Log($"[GameFlow] 槽 {slotIndex} 不存在,忽略读档请求");
            return;
        }
        StartCoroutine(ResumeRoutine(slotIndex));
    }

    private void RequestQuitToMenu()
    {
        if (State != FlowState.InGame)
        {
            Debug.LogWarning("[GameFlow] 返回主菜单请求被拒：当前不在游玩中");
            return;
        }
        StartCoroutine(QuitToMenuRoutine());
    }

    private void RequestEnding(string endingId)
    {
        if (State != FlowState.InGame)
        {
            Debug.LogWarning($"[GameFlow] 结局 {endingId} 触发被拒：当前不在游玩中（State = {State}）");
            return;
        }
        var def = EndingCatalog.Find(endingId);
        if (def == null)
        {
            Debug.LogWarning($"[GameFlow] 结局 {endingId} 未在 EndingCatalog 登记，忽略（加结局 = 目录里加一条）");
            return;
        }
        if (!EndingCatalog.CanTrigger(endingId))
        {
            Debug.LogWarning($"[GameFlow] 结局 {endingId} 尚未解锁（requiresEnding / unlockCheck 未满足），忽略");
            return;
        }
        StartCoroutine(EndingRoutine(def));
    }

    /// <summary>结局门(LevelAnchor Ending 目的地)被刷卡 → 同一入口。</summary>
    private void OnEndingDoorRequested(string endingId)
    {
        RequestEnding(endingId);
    }

    // ==================== 主菜单 ↔ 游玩 ====================

    private IEnumerator NewGameRoutine(int slotIndex)
    {
        yield return FadeOverlay.FadeInAndHold(0.3f);   // 盖幕 + 全黑停留(时长见 FadeOverlay.holdBlackSeconds)

        // 覆盖确认已由菜单 UI 完成:直接清槽,开新局(周目 = 已通关数 + 1)
        SaveSystem.ClearSlot(slotIndex);
        int playthrough = SaveSystem.Meta.completedRuns + 1;
        SaveSystem.BeginNewRunSession(slotIndex, playthrough);

        SetState(FlowState.InGame);
        yield return HideBackdropRoutine();
        LevelTransitionManager.Instance?.BeginRun(0);   // 开局 settle → OnLtmSettled 自动存档
        yield return FadeOverlay.FadeRoutine(0f, 0.4f);
        Debug.Log($"[GameFlow] 新游戏开始：槽 {slotIndex} · 第 {playthrough} 周目");
    }

    private IEnumerator ResumeRoutine(int slotIndex)
    {
        var summary = SaveSystem.DescribeSlot(slotIndex);
        if (!summary.exists)
        {
            Debug.Log($"[GameFlow] 槽 {slotIndex} 为空，无法继续");
            yield break;
        }
        var slot = SaveSystem.ReadSlot(slotIndex);

        yield return FadeOverlay.FadeInAndHold(0.3f);   // 盖幕 + 全黑停留(时长见 FadeOverlay.holdBlackSeconds)

        if (slot == null || slot.runFinished || string.IsNullOrEmpty(slot.levelPath))
        {
            // 已通关 / 内容失效的档:"继续" = 直接开新周目(无确认 —— 档内容本就已清空)
            Debug.Log($"[GameFlow] 槽 {slotIndex} 已通关或内容失效，继续 = 开新周目");
            SaveSystem.ClearSlot(slotIndex);
            int playthrough = SaveSystem.Meta.completedRuns + 1;
            SaveSystem.BeginNewRunSession(slotIndex, playthrough);
            SetState(FlowState.InGame);
            yield return HideBackdropRoutine();
            LevelTransitionManager.Instance?.BeginRun(0);
            yield return FadeOverlay.FadeRoutine(0f, 0.4f);
            yield break;
        }

        // 正式读档:恢复会话(局内键值等) → 关卡加载 → 物件/玩家恢复 → 稳定点(自动存档保险)
        SaveSystem.BeginResumeSession(slotIndex, slot);
        SetState(FlowState.InGame);
        yield return HideBackdropRoutine();

        var ltm = LevelTransitionManager.Instance;
        if (ltm == null || !ltm.BeginResume(slot.levelPath))
        {
            yield return AbortResumeToMenu($"读档失败：无法加载关卡 {slot.levelPath}");
            yield break;
        }
        // 等关卡就绪(轮询场景路径;BeginResume 内部异步,无完成回调)
        int guard = 0;
        while (ltm.CurrentLevelPath != slot.levelPath && guard++ < 1200)
            yield return null;
        if (ltm.CurrentLevelPath != slot.levelPath)
        {
            yield return AbortResumeToMenu($"读档超时：关卡 {slot.levelPath} 未能加载");
            yield break;
        }

        yield return SaveSystem.ApplyRestoreRoutine(slot);   // 场景物件 → 玩家 → 持物重挂
        ltm.SettleAfterRestore();                            // 进入稳定点(Settled → 自动存档保险)
        yield return FadeOverlay.FadeRoutine(0f, 0.4f);
        Debug.Log($"[GameFlow] 读档完成：槽 {slotIndex} · 第 {slot.playthroughNumber} 周目 · {slot.levelPath}");
    }

    /// <summary>
    /// 读档中途失败(关卡加载失败 / 超时)的收尾:清会话 → 卸载可能已加载的玩家与关卡 →
    /// 状态回主菜单并重挂背景 —— 不让玩家在黑屏滞留(之前只能盲按 Esc 从暂停自救)。
    /// </summary>
    private IEnumerator AbortResumeToMenu(string reason)
    {
        Debug.LogError($"[GameFlow] {reason} → 已回主菜单");
        SaveSystem.EndRunSession();   // 会话已在 BeginResumeSession 开启,回菜单前清掉
        var ltm = LevelTransitionManager.Instance;
        if (ltm != null)
            yield return ltm.EndRunToMenuCoroutine();   // 卸载已加载的玩家/关卡,复位内部状态
        SetState(FlowState.MainMenu);
        yield return ShowBackdropRoutine(ResolveDefaultBackdropPath());
        ReleaseCursor();
        yield return FadeOverlay.FadeRoutine(0f, 0.4f);
    }

    private IEnumerator QuitToMenuRoutine()
    {
        PauseMenu.ForceExitPause();   // 若从暂停菜单发起:先解除暂停(timeScale=1 / 光标释放)
        yield return FadeOverlay.FadeInAndHold(0.35f);   // 盖幕 + 全黑停留(时长见 FadeOverlay.holdBlackSeconds)

        // 返回主菜单前自动存档(防崩溃丢进度)。只在场景装卸的临时窗口等一等 —— T0→T1 加载 /
        // T3 卸载 / 开局收局占用,都要先落定现场才能采集,硬存必失败且随后 EndRunSession 会静默丢进度;
        // 上限 ~30 秒(加载卡死等不到时放弃存档并告警,不阻塞回菜单)。
        // 不用 IsSettled 当"过渡中":它还包含"门已开、等玩家穿门"—— 那状态玩家可以一直停留
        // (刷卡后走开不穿门),等它 = 回菜单被无限期挡住(表现:点完按钮黑屏几十秒没反应)。
        var ltm = LevelTransitionManager.Instance;
        if (ltm != null)
        {
            float wait = 0f;
            while (ltm.IsSceneTransitionInFlight && wait < 30f)
            {
                yield return null;
                wait += Time.unscaledDeltaTime;
            }
            bool saved = ltm.IsSettled && SaveSystem.SaveActiveSlot();
            if (!saved)
                Debug.LogWarning("[GameFlow] 返回主菜单前自动存档失败(现场此刻不可采集:装卸中 / 门已开未穿门 / 未就绪),本次进度未落盘");
        }

        if (ltm != null)
            yield return ltm.EndRunToMenuCoroutine();   // 卸载关卡 + 玩家场景 + 资源清理
        SaveSystem.EndRunSession();

        SetState(FlowState.MainMenu);
        yield return ShowBackdropRoutine(ResolveDefaultBackdropPath());
        // 主菜单页面显隐由你搭的按钮 / MainMenuUI 决定(流程只管状态与背景)
        ReleaseCursor();
        yield return FadeOverlay.FadeRoutine(0f, 0.4f);
    }

    // ==================== 结局 ====================

    private IEnumerator EndingRoutine(EndingCatalog.EndingDefinition def)
    {
        PauseMenu.ForceExitPause();   // 防御:若触发于暂停中先解除
        yield return FadeOverlay.FadeInAndHold(0.4f);   // 盖幕 + 全黑停留(时长见 FadeOverlay.holdBlackSeconds)

        currentEndingId = def.id;
        endingRunPlaythrough = SaveSystem.HasActiveRun ? SaveSystem.ActivePlaythrough : SaveSystem.Meta.completedRuns + 1;

        SetState(FlowState.Ending);
        GameEvents.EndingReached?.Invoke(def.id);

        var ltm = LevelTransitionManager.Instance;
        if (ltm != null)
            yield return ltm.EndRunToMenuCoroutine();   // 收局:卸载关卡 + 玩家场景
        // 注意:活动档会话保留到 EndingConfirmed(要拿周目号结算 / 置已通关)

        // Room_00 只做主菜单背景,不担任结局演出地 —— 结局默认纯色幕(EndingOverlay 自带全屏底色);
        // 只在结局定义显式给出 backdropScenePath(专属演出场景)时才加载它。
        bool sceneBackdrop = !string.IsNullOrEmpty(def.backdropScenePath);
        if (sceneBackdrop)
            yield return ShowBackdropRoutine(def.backdropScenePath);
        EndingOverlay.Instance?.Show(def, !sceneBackdrop);
        ReleaseCursor();
        yield return FadeOverlay.FadeRoutine(0f, 0.4f);
        Debug.Log($"[GameFlow] 结局演出开始：{def.id}");
    }

    private void ConfirmEnding()
    {
        if (State != FlowState.Ending || string.IsNullOrEmpty(currentEndingId)) return;

        // 结算跨局元数据:累计通关周目只增不减 + 结局去重入库 + 活动档置"已通关"
        SaveSystem.RecordCompletedRun(endingRunPlaythrough);
        SaveSystem.RecordEndingSeen(currentEndingId);
        SaveSystem.FinishActiveSlot();
        SaveSystem.EndRunSession();
        Debug.Log($"[GameFlow] 结局确认：{currentEndingId} · 累计通关 {SaveSystem.Meta.completedRuns} 周目");

        string endId = currentEndingId;
        currentEndingId = "";
        EndingOverlay.Instance?.Hide();
        StartCoroutine(ReturnToMenuRoutine(endId));
    }

    /// <summary>结局确认后回主菜单:结局用了非默认背景时换回 Room_00。</summary>
    private IEnumerator ReturnToMenuRoutine(string endedId)
    {
        yield return FadeOverlay.FadeInAndHold(0.3f);   // 盖幕 + 全黑停留(时长见 FadeOverlay.holdBlackSeconds)
        string defBackdrop = ResolveDefaultBackdropPath();
        if (currentBackdropPath != defBackdrop)
            yield return ShowBackdropRoutine(defBackdrop);
        SetState(FlowState.MainMenu);   // 主菜单页面显隐由你搭的按钮 / MainMenuUI 决定
        ReleaseCursor();
        yield return FadeOverlay.FadeRoutine(0f, 0.4f);
        Debug.Log($"[GameFlow] 已回主菜单（结局 {endedId} 已记录）");
    }

    // ==================== 稳定点自动存档 ====================

    private void OnLtmSettled()
    {
        // 只响应"游玩中 + 有活动档"的稳定点(开局到达 / 每关到达站稳 / 读档恢复完)
        if (State != FlowState.InGame || !SaveSystem.HasActiveRun) return;
        if (autosaveQueued) return;
        autosaveQueued = true;
        StartCoroutine(SettleAutosaveRoutine());
    }

    private IEnumerator SettleAutosaveRoutine()
    {
        // 等 ~2 帧:物理 / 门落定后再采集(滑门不可能停在半程,持物镜像已新鲜)
        yield return null;
        yield return null;
        SaveSystem.SaveActiveSlot();
        autosaveQueued = false;
    }

    // ==================== 背景场景(主菜单 / 结局演出) ====================

    private static string ResolveDefaultBackdropPath()
    {
        for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
        {
            var p = SceneUtility.GetScenePathByBuildIndex(i);
            if (p.EndsWith("/" + SaveSchema.BackdropSceneFileName))
                return p;
        }
        var inst = Instance;
        if (inst != null && !inst.warnedMissingBackdrop)
        {
            inst.warnedMissingBackdrop = true;
            Debug.LogWarning($"[GameFlow] 背景场景 {SaveSchema.BackdropSceneFileName} 未加入 Build Settings（File → Build Settings → Scenes in Build）。主菜单 / 结局将无背景画面。");
        }
        return "";
    }

    /// <summary>卸载旧背景(若有)→ 加载新背景 → 设为活动场景 → 备好菜单相机(场景无相机则自建)。</summary>
    private IEnumerator ShowBackdropRoutine(string backdropPath)
    {
        if (string.IsNullOrEmpty(backdropPath))
        {
            // 背景缺失:尽力而为 —— 无背景也照常出菜单(纯色底)
            currentBackdropPath = "";
            SetMenuCameraActive(false);
            yield break;
        }

        if (currentBackdropPath == backdropPath && IsSceneLoadedByPath(backdropPath))
        {
            SetMenuCameraActive(true);
            yield break;
        }

        yield return HideBackdropRoutine();

        var op = SceneManager.LoadSceneAsync(backdropPath, LoadSceneMode.Additive);
        if (op == null)
        {
            Debug.LogError($"[GameFlow] 背景场景加载失败（op 为 null）：{backdropPath}");
            yield break;
        }
        yield return op;

        currentBackdropPath = backdropPath;
        SceneManager.SetActiveScene(SceneManager.GetSceneByPath(backdropPath));
        EnsureMenuCamera();
        SetMenuCameraActive(true);
    }

    private IEnumerator HideBackdropRoutine()
    {
        SetMenuCameraActive(false);
        if (string.IsNullOrEmpty(currentBackdropPath)) yield break;
        var scene = SceneManager.GetSceneByPath(currentBackdropPath);
        if (scene.IsValid() && scene.isLoaded)
        {
            var op = SceneManager.UnloadSceneAsync(scene);
            if (op != null) yield return op;
        }
        currentBackdropPath = "";
        menuCamera = null;   // 相机是背景场景的物体,已随卸载销毁
    }

    private static bool IsSceneLoadedByPath(string path)
    {
        var s = SceneManager.GetSceneByPath(path);
        return s.IsValid() && s.isLoaded;
    }

    // === 菜单相机(背景场景无相机时运行时自建;作者在场景里自己摆相机则优先用作者的) ===
    private void EnsureMenuCamera()
    {
        var scene = SceneManager.GetSceneByPath(currentBackdropPath);
        if (!scene.IsValid() || !scene.isLoaded) return;

        // 优先用场景里作者摆好的相机(艺术构图可控)
        foreach (var root in scene.GetRootGameObjects())
        {
            var cam = root.GetComponentInChildren<Camera>(true);
            if (cam != null && cam.isActiveAndEnabled)
            {
                menuCamera = cam.gameObject;
                cameraOwned = false;
                return;
            }
        }

        // 自建占位相机:位置/朝向是常量,作者可改下方常量或在场景里放自己的相机覆盖
        menuCamera = new GameObject("Menu Camera");
        SceneManager.MoveGameObjectToScene(menuCamera, scene);
        cameraOwned = true;

        var camera = menuCamera.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.02f, 0.02f, 0.03f, 1f);
        camera.fieldOfView = 60f;
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 100f;
        menuCamera.AddComponent<HDAdditionalCameraData>();   // HDRP 相机必需
        if (menuCamera.GetComponent<AudioListener>() == null)
            menuCamera.AddComponent<AudioListener>();        // 玩家场景卸载后唯一的监听器

        // 占位构图常量(朝向 Room_00 的原点方向;作者要正式构图请改这里或摆自己的相机)
        menuCamera.transform.position = new Vector3(0f, 1.8f, 7.5f);
        menuCamera.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
    }

    private void SetMenuCameraActive(bool active)
    {
        if (menuCamera == null) return;
        var cam = menuCamera.GetComponent<Camera>();
        if (cam != null) cam.enabled = active;
        var listener = menuCamera.GetComponent<AudioListener>();
        if (listener != null) listener.enabled = active;
        if (active && cameraOwned)
            menuCamera.SetActive(true);
    }
}
