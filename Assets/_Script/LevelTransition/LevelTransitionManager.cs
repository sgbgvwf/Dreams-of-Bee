using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// 关卡切换管理器（单例）：无缝关卡推进的核心状态机。挂在 Persistance 启动场景的 LevelManager 上。
/// 流程(主菜单 / 读档 / 结局)由 GameFlowManager 编排,本类只负责"把某关跑起来"与关间过渡:
///
/// 关卡模型(单向推进,出口数据驱动 —— 新增关卡的作者指南见文件尾):
///   - 关卡图是非线性的(分支 / 回环都可以):任何一关都可以是任何一关的前一站,关卡之间没有
///     "默认下一关"这种东西 —— 去哪只由刷卡的那张卡说了算;
///   - 关卡注册表 = Inspector 的 levels 列表(只是"有哪些关"的登记 + 直达序数,不代表推进顺序),
///     失效时从 Build Settings 推导(自动跳过本场景 / 玩家场景 / 菜单背景 Room_00);
///   - 每道出口门(出口锚点 LevelAnchor,一个场景可有多道)的去向由刷卡钥匙(卡片)携带:
///     Scene(显式指定目标关卡) / Ending(结局)。卡没配/配了缺目标 → 报错拒绝,没有兜底去向;
///   - 当前关身份 = 场景路径(存档 / 读档的事实来源),currentLevelIndex 只是注册表序数(展示用)。
///     出口门不持有任何关卡标注 —— 门洞预制体自包含,实例摆在哪个场景就属于哪一关(见 ResolveExitsForCurrentLevel)。
///
/// 运行生命周期(由 GameFlowManager 驱动):
///   - BeginRun(i)   : 开局(菜单→新游戏):并行加载玩家场景 + 第 i 关,锁好出口门后 Settled;
///   - BeginResume(path): 读档:加载玩家场景 + 指定关卡(不 Settled —— 等流程恢复完场景物件与玩家,
///                     由流程调 SettleAfterRestore());
///   - EndRunToMenuCoroutine(): 收局:卸载关卡（含刷卡后没穿门时已加载的目的地关）与玩家场景、
///                     清理资源、复位内部状态。
///   - Settled / IsSettled: 无任何加载 / 卸载 / 待穿越过渡的稳定点。刷卡、存档都只在 Settled 允许;
///     每次"到达新关站稳"(Begin* 完成 / T2 穿越 + T3 卸载完成)会触发 Settled 事件 → 流程自动存档。
///
/// 通用请求区(封装层 —— 一切"转换到别的场景"的公开入口,见 Request* 方法):
///   - RequestExitKeyed(door[, key]):刷卡门(门演出路径,T0–T3)。目的地 = 刷卡钥匙(Card)携带,
///     刷卡瞬间裁决并加载(最近一次刷卡生效;过渡中未穿过前换卡重刷 = 关门卸旧目标、载新目标;
///     卡没配目的地 / 无卡 → 报错拒绝,目的地唯一权威是卡);
///   - RequestDirectSwitch(path[, landing]):无门直达换场景(无演出;调试 / 演示 / 选关用;
///     可选落点 = 目标关出生点 PlayerSpawnPoint / 保持原位,见 PlayerLanding);
///   - 结局 / 主菜单请求跨流程状态与存档收局,归 GameFlowManager(TriggerEnding / QuitToMenu),
///     刷卡门"结局门"(卡的 Destination = Ending)仍经 EndingRequested 事件交给流程;
///   - 玩家落点分工:门演出路径的落点由 PortalDoor + 目标关 Entry 锚点负责(相对位姿映射);
///     开局 / 直达的落点由本层 PlayerSpawnPoint 机制负责(有标记才传送,缺省保持旧行为)。
///     钥匙归属(关卡/身份)归读卡器校验;"刷卡去哪" = 卡上目的地,本层裁决执行。
/// 每场景配套的薄 Facade 组件见 SceneTransition.cs(场景 UI 按钮经它调用上述请求,可配置直达落点)。
///
/// 过渡时序(严格遵循,保持原样):
///   T0 玩家在出口读卡器上刷卡成功 → 后台异步加载目的地关(门保持关闭且锁定 —— 关闭的门本身就是屏障)
///   T1 加载完成 → 激活新场景、解析入口锚点 → 门自动滑开 → 激活出口锚点上的传送门(PortalDoor)
///      （门打开的瞬间，门面上的门后渲染纹理已实时显示目的地关，玩家看到完整画面的瞬间它已存在）
///   T2 玩家穿过门洞 → PortalDoor 把玩家传送到入口门洞（速度 / 朝向同步换算）→ 结算过渡
///   T3 结算后立即异步卸载上一关 → 清理资源（门是单向的：前场景已卸载，无法返回）
///
/// 无缝原理：各关独立摆放在自己的世界坐标，门洞由传送门系统渲染
/// （PortalDoor 用"相对门的位置与玩家相对门的位置相同"的相机生成门面纹理），
/// 穿过瞬间玩家被传送到下一关门洞的对应位置，画面天然连续。
///
/// 防异常：防重复加载（loadState / busyActive）、防重复卸载/结算（unloading / passFinalized）、
/// 卸载期间刷卡挂起（pendingPortal 续传）、恢复/过渡期拒绝刷卡、最后一关无出口空值安全。Play Mode only。
///
/// 新增一关(非线性关卡图)全流程:
///   1) 新建场景并摆内容; 2) 加入 Build Settings 并拖进本管理器 Inspector 的 Levels 列表(登记在册,
///      顺序只影响直达序数 / 展示); 3) 场景入口门洞放 Entry 锚点; 4) 出口门洞放 Exit 锚点(挂
///      SlidingDoor 门引用 + PortalDoor);
///   5) 出口门不需要任何关卡标注 —— 门洞预制体(移动门)自包含:锚点引用同一实例里的门,复制实例到
///      新关引用自动跟着走(管理器解析出口时只要求门活在本关场景里,挡"拖成预制体资产"这类误配);
///   6) 每张出口卡配好目的地(Card 组件:Scene 拖目标关卡 / Ending 填结局 id),刷卡通过 → 去卡上
///      目的地;换卡重刷 = 关门换目标。核心零改动。
///   (可选)开局 / 直达的落点:关卡开局位置摆一个 PlayerSpawnPoint 出生点 —— 有标记时
///   BeginRun / 带落点的 RequestDirectSwitch 把玩家放到那,没标记时保持旧行为(原位),不摆也能跑。
/// </summary>
public class LevelTransitionManager : MonoBehaviour
{
    public static LevelTransitionManager Instance { get; private set; }

    /// <summary>当前关卡在注册表中的序数（-1 = 当前关不在注册表，如分支目的地；展示 / 直达序数用）。</summary>
    public int CurrentLevelIndex => currentLevelIndex;

    /// <summary>当前关卡场景路径（存档 / 读档的唯一事实来源；空 = 无当前关卡）。</summary>
    public string CurrentLevelPath => currentScene.IsValid() && currentScene.isLoaded ? currentScene.path : "";

    /// <summary>当前关卡场景（SaveSystem 采集场景物件状态用）。</summary>
    public Scene CurrentLevelScene => currentScene;

    /// <summary>关卡注册表长度（Inspector levels 登记序；场景组件 / UI / 内容读取用）。</summary>
    public int LevelCount => levelPaths != null ? levelPaths.Length : 0;

    /// <summary>关卡注册表第 index 关的场景路径（越界返回空串）。</summary>
    public string GetLevelPath(int index) =>
        levelPaths != null && index >= 0 && index < levelPaths.Length ? levelPaths[index] : "";

    /// <summary>场景路径在关卡注册表中的序数（不在注册表 = -1，如分支目的地）。</summary>
    public int IndexOfLevel(string path) => IndexOfPath(path);

    /// <summary>玩家场景是否已加载（常驻玩法场景，可随局卸载重载）。</summary>
    public bool IsPlayerSceneLoaded
    {
        get
        {
            if (string.IsNullOrEmpty(playerScenePath)) return false;
            var s = SceneManager.GetSceneByPath(playerScenePath);
            return s.IsValid() && s.isLoaded;
        }
    }

    /// <summary>稳定点：无加载 / 卸载 / 待穿越过渡。刷卡与存档只允许在 Settled 时进行。</summary>
    public bool IsSettled => settled;

    /// <summary>场景装卸正在进行（临时窗口，必然自行落定）：T0→T1 加载中 / T3 卸载中 / 开局收局流程占用中。
    /// 与 IsSettled 的分工：IsSettled 还把"门已开、等玩家穿门"也算作非稳定，而那个状态玩家可以一直停留
    /// （刷卡后走开不穿门，关卡本身仍然是稳定的），不是过渡窗口。流程收局要"等一下再动手"时读本属性，
    /// 读 IsSettled 会把可以无限期的状态误当成过渡窗口去等。</summary>
    public bool IsSceneTransitionInFlight => loadState == LoadState.Loading || unloading || busyActive;

    /// <summary>进入稳定点时触发（Begin* 完成 / T2+T3 穿越结算完成）。GameFlowManager 借此做"到达自动存档"。</summary>
    public event System.Action Settled;

    /// <summary>结局门（出口目的地 = Ending）被刷卡时触发（载荷：结局 id）。无监听者（开发者直玩等）则只开门不换场。</summary>
    public event System.Action<string> EndingRequested;

    // === 一口出口的运行时配置（一个出口锚点 = 一扇门 + 一个可选的传送门） ===
    private sealed class ExitPortal
    {
        public LevelAnchor anchor;      // 出口锚点（门洞参照 / 穿门基准）
        public SlidingDoor door;        // 出口门
        public PortalDoor portal;       // 出口锚点上的传送门（T1 激活；可空）
    }

    /// <summary>刷卡瞬间从卡裁决出的目的地（Card 携带；本层只负责装载执行）。</summary>
    private readonly struct ResolvedDestination
    {
        public readonly LevelAnchor.DestinationKind kind;
        public readonly string scenePath;   // kind = Scene 时有效
        public readonly string endingId;    // kind = Ending 时有效

        public ResolvedDestination(LevelAnchor.DestinationKind kind, string scenePath, string endingId)
        {
            this.kind = kind;
            this.scenePath = scenePath;
            this.endingId = endingId;
        }
    }

#if UNITY_EDITOR
    [SerializeField, Tooltip("关卡注册表：本游戏用到的全部关卡，Inspector 按顺序拖入（不含 Persistance 启动场景与 Room_00 背景）。顺序只决定直达序数（选关 / GoToLevel / 开局起点），不是推进顺序 —— 非线性关卡图里\"下一关是哪一关\"由刷卡那张卡的目的地决定。")]
    private SceneAsset[] levels;
#endif

    // 运行时用场景路径加载（SceneAsset 是编辑器专属类型，构建后为 null，故镜像为路径）
    [SerializeField, HideInInspector]
    private string[] levelPaths;

#if UNITY_EDITOR
    [SerializeField, Tooltip("玩家场景（Player.unity）：开局加载一次、随局卸载，与 Persistance 不同的常驻机制（不调用 DontDestroyOnLoad，关卡卸载只针对关卡场景）")]
    private SceneAsset playerScene;
#endif

    [SerializeField, HideInInspector]
    private string playerScenePath;

    [SerializeField, Tooltip("仅测试用：模拟加载延迟（秒），用于观察‘门保持关闭直到加载完成’的时序")]
    private float simulatedLoadDelay;

    [SerializeField, Tooltip("拾卡后门最少保持关闭的时长（秒）：即使下一关瞬间加载完，门也先关够这段时间再开，让‘门是屏障、加载完成后才放行’的顺序可见。设为 0 则加载完立即开门")]
    private float minDoorCloseTime = 0.4f;

    // --- 状态 ---
    private enum LoadState { Idle, Loading, Done }              // 加载状态

    private int currentLevelIndex = -1;         // 当前关在注册表里的序数（-1 = 不在注册表，如分支目的地）
    private LoadState loadState = LoadState.Idle;       // 加载状态
    private bool unloading;                     // 是否正在卸载（防重复卸载；卸载期间刷卡 → 挂起）
    private bool passFinalized;                 // 本次过渡是否已结算（防重复结算）
    private bool transitionTriggered;           // 本关是否已刷卡触发过渡（防重复触发）
    private bool settled;                       // 稳定点（无任何加载 / 卸载 / 待穿越过渡）
    private bool busyActive;                    // 生命周期协程(Begin*/EndRun)占用中,防重入
    private ExitPortal pendingPortal;           // 卸载期间刷的新出口门 → 卸载完成后续传
    private ResolvedDestination pendingDestination;  // 与 pendingPortal 配套的刷卡目的地(卸载窗口暂存)
    private Coroutine loadRoutine;              // 当前 T0→T1 加载协程(换卡重刷时先等它退场)
    private AsyncOperation loadOp;              // 当前加载的异步操作(换卡重刷时放行旧加载)
    private int loadGeneration;                 // 加载代际:每次发起过渡自增;协程凭代际识别自己被换掉
    private string activeDestPath;              // 本次过渡正在加载/已加载的目标场景路径(换卡重刷时卸载旧目标用)

    private Scene currentScene;                 // 当前关卡场景
    private Scene nextScene;                    // 正在加载的目的地关场景（T1 激活目标）
    private Scene prevScene;                    // 上一关卡场景（T3 卸载目标）
    private readonly List<ExitPortal> exitPortals = new List<ExitPortal>();   // 当前关的全部出口（每道门上锁）
    private ExitPortal activePortal;            // 本次过渡的出口（T0 刷卡的那道门）
    private Transform entryAnchor;              // 目的地关入口锚点（T1 解析，传送门位姿基准）

    private void Awake()
    {
        // 单例：防重复实例（场景重载 / 多管理器）
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        // 只做关卡列表校验（不再自动开局 —— 开局由 GameFlowManager 决定时机）。
        // levelPaths 序列化引用若失效（levels 的 SceneAsset 引用未解析成功，OnValidate 会把
        // levelPaths 写成 null 条目），则从 Build Settings 推导（按路径跳过常驻场景：本管理器所在
        // 场景 Persistance + 玩家场景 Player + 菜单背景 Room_00，不按序号假设）。
        if (levelPaths == null || levelPaths.Length == 0 || string.IsNullOrEmpty(levelPaths[0]))
        {
            var fallback = new List<string>();
            string ownScenePath = gameObject.scene.path;
            for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
            {
                var p = SceneUtility.GetScenePathByBuildIndex(i);
                if (string.IsNullOrEmpty(p) || p == ownScenePath || p == playerScenePath) continue;
                if (p.EndsWith("/" + SaveSchema.BackdropSceneFileName)) continue;   // 背景场景(Room_00)永不进关卡列表
                fallback.Add(p);
            }
            if (fallback.Count > 0)
            {
                levelPaths = fallback.ToArray();
                Debug.LogWarning($"[LevelTransitionManager] levelPaths 序列化引用失效，已从 Build Settings 推导 {levelPaths.Length} 个关卡（请检查 Inspector 中 Levels 列表的引用）", this);
            }
            else
            {
                Debug.LogError("[LevelTransitionManager] 关卡列表为空且 Build Settings 无可用关卡，游戏无法推进", this);
                return;
            }
        }
        else
        {
            // 过滤掉数组里的空条目（防御 OnValidate 写入的 null）
            var cleaned = new List<string>();
            foreach (var p in levelPaths)
                if (!string.IsNullOrEmpty(p)) cleaned.Add(p);
            levelPaths = cleaned.ToArray();
        }

        Debug.Log($"[LevelTransitionManager] 关卡顺序: {string.Join(" → ", levelPaths)}", this);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        // 玩家场景镜像为路径（运行时按路径加载；未加入 Build Settings 则无法加载）。
        // 与 levels 独立处理：levels 清空时玩家场景引用不应被跳过。
        if (playerScene != null)
        {
            playerScenePath = AssetDatabase.GetAssetPath(playerScene);
            if (!IsInBuildSettings(playerScenePath))
                Debug.LogWarning($"[LevelTransitionManager] 玩家场景 {playerScenePath} 未加入 Build Settings（File → Build Settings → Scenes in Build），启动时无法加载玩家", this);
        }

        // 把 Inspector 里的 SceneAsset 镜像为路径数组（运行时只走路径加载）
        if (levels == null) return;
        levelPaths = new string[levels.Length];
        for (int i = 0; i < levels.Length; i++)
        {
            if (levels[i] == null)
            {
                // 引用未解析：明确警告，并跳过该条目（运行时 Start 有从 Build Settings 推导的兜底）
                Debug.LogWarning($"[LevelTransitionManager] 关卡列表第 {i + 1} 项引用为空，请检查 Inspector 中 Levels 列表", this);
                continue;
            }
            levelPaths[i] = AssetDatabase.GetAssetPath(levels[i]);
            // 编辑期提醒：未注册进 Build Settings 的关卡无法按路径加载
            if (!IsInBuildSettings(levelPaths[i]))
                Debug.LogWarning($"[LevelTransitionManager] 关卡 {levelPaths[i]} 未加入 Build Settings（File → Build Settings → Scenes in Build）", this);
        }
    }

    private static bool IsInBuildSettings(string path)
    {
        foreach (var s in EditorBuildSettings.scenes)
            if (s.path == path) return true;
        return false;
    }
#endif

    // ==================== 运行生命周期（GameFlowManager 驱动） ====================

    /// <summary>开局：加载玩家场景 + 注册表第 startLevelIndex 关（新游戏 / 读档的起点），锁好出口后进入稳定点。</summary>
    public bool BeginRun(int startLevelIndex)
    {
        if (busyActive)
        {
            Debug.LogWarning("[LevelTransitionManager] 已有开局/收局流程进行中，忽略 BeginRun", this);
            return false;
        }
        busyActive = true;
        StartCoroutine(BeginRunCoroutine(startLevelIndex));
        return true;
    }

    private IEnumerator BeginRunCoroutine(int startLevelIndex)
    {
        // 玩家场景与关卡并行加载（互不依赖，比串行快约一半；两个协程交错推进）：
        // 玩家碰撞体注入有 Interactable.ReapplyPlayerCollisionIgnore 兜底，
        // 即使玩家晚于关卡激活，拾取物的碰撞忽略也会补上，时序不再敏感。
        var playerOp = StartCoroutine(LoadPlayerSceneCoroutine());
        var levelOp = StartCoroutine(LoadLevelByIndexCoroutine(startLevelIndex));
        yield return playerOp;
        yield return levelOp;

        if (!currentScene.IsValid() || !currentScene.isLoaded)
        {
            Debug.LogError("[LevelTransitionManager] 开局失败：关卡场景未就绪", this);
            busyActive = false;
            yield break;
        }

        // 一帧:等新场景组件 Start 跑完(同读档 ApplyRestoreRoutine 的等待),否则玩家 Start 的
        // CaptureInitialLook 会在出生点传送之后才执行、覆盖出生朝向
        yield return null;

        // 玩家放到目标关默认出生点(PlayerSpawnPoint;没摆标记 → 保持旧行为:Player 场景编好的位姿)
        PlacePlayerAtSpawn(currentScene, PlayerLanding.LevelSpawnPoint);

        // 解析当前关全部出口门并预锁（下一段过渡的门口）
        ResolveExitsForCurrentLevel();
        SetSettled(true);
        busyActive = false;
        Debug.Log($"[LevelTransitionManager] 开局完成：当前关卡 = {currentScene.name}", this);
    }

    /// <summary>读档：加载玩家场景 + 指定关卡（按路径）。恢复场景物件 / 玩家由流程完成，完成后调 SettleAfterRestore。</summary>
    public bool BeginResume(string levelScenePath)
    {
        if (busyActive)
        {
            Debug.LogWarning("[LevelTransitionManager] 已有开局/收局流程进行中，忽略 BeginResume", this);
            return false;
        }
        if (string.IsNullOrEmpty(levelScenePath))
        {
            Debug.LogError("[LevelTransitionManager] BeginResume 收到空场景路径", this);
            return false;
        }
        busyActive = true;
        StartCoroutine(BeginResumeCoroutine(levelScenePath));
        return true;
    }

    private IEnumerator BeginResumeCoroutine(string levelScenePath)
    {
        var playerOp = StartCoroutine(LoadPlayerSceneCoroutine());
        var levelOp = StartCoroutine(LoadLevelByPathCoroutine(levelScenePath));
        yield return playerOp;
        yield return levelOp;

        if (!currentScene.IsValid() || !currentScene.isLoaded)
        {
            Debug.LogError($"[LevelTransitionManager] 读档失败：关卡场景未就绪 {levelScenePath}", this);
            busyActive = false;
            yield break;
        }
        busyActive = false;
        Debug.Log($"[LevelTransitionManager] 读档场景已加载：{currentScene.name}（等流程恢复物件/玩家状态）", this);
        // 注意：这里不 SetSettled —— 恢复期间 IsSettled=false，刷卡一律拒绝，
        // 等流程 ApplyRestore 完成再调 SettleAfterRestore()。
    }

    /// <summary>流程恢复完场景物件与玩家状态后调用：重新解析出口门并进入稳定点。</summary>
    public void SettleAfterRestore()
    {
        if (!currentScene.IsValid() || !currentScene.isLoaded)
        {
            Debug.LogError("[LevelTransitionManager] SettleAfterRestore 但关卡场景未就绪", this);
            return;
        }
        ResolveExitsForCurrentLevel();
        SetSettled(true);
        Debug.Log($"[LevelTransitionManager] 读档恢复完成，进入稳定点：{currentScene.name}", this);
    }

    /// <summary>收局（返回主菜单 / 结局）：卸载当前关、已刷卡但没穿门时已加载的目的地关、玩家场景，
    /// 清理资源并复位内部状态。流程以协程方式运行本方法。</summary>
    public IEnumerator EndRunToMenuCoroutine()
    {
        busyActive = true;
        SetSettled(false);

        // 还挂在半途的加载（流程等待超时后放弃等待时会走到这里）：先夺代际令加载协程在自检点退场
        // （不再开门 / 激活传送门），再放行激活等它落地 —— 下面按路径把它加载的目的地关一并卸载，
        // 不留半途的加载窗口（否则它会在收局之后才激活，成为菜单里的残留场景）
        loadGeneration++;
        var pendingLoad = loadOp;
        if (pendingLoad != null && !pendingLoad.isDone)
        {
            pendingLoad.allowSceneActivation = true;
            while (!pendingLoad.isDone) yield return null;
        }

        // 卸载当前关卡（若有）
        if (currentScene.IsValid() && currentScene.isLoaded)
        {
            var op = SceneManager.UnloadSceneAsync(currentScene);
            if (op != null) yield return op;
        }

        // 卸载"已刷卡但玩家没穿门"的目的地关（T0/T1 已加载进来，但还不是 currentScene）：
        // 不在这里收掉，它就会跟着回到主菜单 —— 下次再进同一关会加载出重复实例
        if (!string.IsNullOrEmpty(activeDestPath))
        {
            var dest = SceneManager.GetSceneByPath(activeDestPath);
            if (dest.IsValid() && dest.isLoaded && dest != currentScene)
            {
                var op = SceneManager.UnloadSceneAsync(dest);
                if (op != null) yield return op;
            }
        }

        // 卸载玩家场景（若有）
        yield return UnloadPlayerSceneCoroutine();

        Resources.UnloadUnusedAssets();

        // 复位内部状态
        currentScene = default;
        nextScene = default;
        prevScene = default;
        currentLevelIndex = -1;
        exitPortals.Clear();
        activePortal = null;
        entryAnchor = null;
        pendingPortal = null;
        pendingDestination = default;
        loadRoutine = null;
        loadOp = null;
        activeDestPath = null;
        loadGeneration++;   // 令任何残留加载协程在自检点退出
        transitionTriggered = false;
        passFinalized = false;
        unloading = false;
        loadState = LoadState.Idle;
        busyActive = false;
        Debug.Log("[LevelTransitionManager] 收局完成：关卡与玩家场景已卸载，状态复位", this);
    }

    /// <summary>
    /// 加载玩家场景（Player.unity）为常驻玩法场景：开局时加载一次、随局卸载（EndRun），
    /// 其中的玩家（含准星 Canvas / 场景 EventSystem 子物体）跨关卡持续存在 —— 不调用
    /// DontDestroyOnLoad，关卡卸载（T3）只针对关卡场景，玩家场景只被 EndRun 卸载。
    /// 防重复：场景已加载则直接跳过（编辑器里先手开了玩家场景再进 Play 不二次加载）。
    /// </summary>
    private IEnumerator LoadPlayerSceneCoroutine()
    {
        if (string.IsNullOrEmpty(playerScenePath))
        {
            Debug.LogWarning("[LevelTransitionManager] 未配置玩家场景（Inspector → Player Scene 为空），将没有玩家。请把 Player.unity 拖入该字段并确认已加入 Build Settings", this);
            yield break;
        }

        var already = SceneManager.GetSceneByPath(playerScenePath);
        if (already.IsValid() && already.isLoaded)
        {
            Debug.Log($"[LevelTransitionManager] 玩家场景已在加载列表中，跳过重复加载：{playerScenePath}", this);
            yield break;
        }

        var op = SceneManager.LoadSceneAsync(playerScenePath, LoadSceneMode.Additive);
        if (op == null)
        {
            Debug.LogError($"[LevelTransitionManager] 玩家场景加载失败（op 为 null，请确认已加入 Build Settings）：{playerScenePath}", this);
            yield break;
        }
        yield return op;

        Debug.Log($"[LevelTransitionManager] 玩家场景已加载：{playerScenePath}", this);
    }

    /// <summary>卸载玩家场景（EndRun 用；未加载则跳过）。</summary>
    private IEnumerator UnloadPlayerSceneCoroutine()
    {
        if (string.IsNullOrEmpty(playerScenePath)) yield break;
        var s = SceneManager.GetSceneByPath(playerScenePath);
        if (!s.IsValid() || !s.isLoaded) yield break;
        var op = SceneManager.UnloadSceneAsync(s);
        if (op != null) yield return op;
        Debug.Log("[LevelTransitionManager] 玩家场景已卸载", this);
    }

    private void ResolveExitsForCurrentLevel()
    {
        exitPortals.Clear();

        foreach (var anchor in LevelAnchor.FindAllAnchors(currentScene, LevelAnchor.AnchorType.Exit))
        {
            var door = anchor.ResolveExitDoor();

            // 本地性断言：门必须是"活在本关场景里"的实例。关卡图非线性，门不属于"第几关"这种
            // 序号概念 —— 门洞预制体（移动门）自包含，实例摆在哪个场景就属于哪一关，所以这里只问
            // "门自己活在哪个场景"。挡住的是把门字段拖成预制体资产（Project 窗口里的资产，不是场景
            // 实例）这类误配：那种情况下门永远不动且不报错，最难查。
            if (door != null && door.gameObject.scene != currentScene)
            {
                Debug.LogError($"[LevelTransitionManager] {currentScene.name} 的出口锚点 {anchor.name} 引用的门 {door.name} 不在本关场景（多半是拖成了预制体资产），忽略该门", this);
                door = null;
            }

            if (door == null)
            {
                Debug.Log($"[LevelTransitionManager] {currentScene.name} 的出口锚点 {anchor.name} 没有可用出口门，跳过", this);
                continue;
            }

            var portal = anchor.GetComponentInChildren<PortalDoor>(true);
            exitPortals.Add(new ExitPortal { anchor = anchor, door = door, portal = portal });
            door.SetLocked(true);   // 门默认锁定，玩家无法通过
        }

        if (exitPortals.Count == 0)
            Debug.Log($"[LevelTransitionManager] {currentScene.name} 没有出口门（最后一关或未配置）", this);
        else
            Debug.Log($"[LevelTransitionManager] {currentScene.name} 出口门已解析并锁定，共 {exitPortals.Count} 道", this);
    }

    // ==================== 玩家落点（无门转换的开局 / 直达落点；门演出路径不受影响） ====================

    /// <summary>关卡转换完成后的玩家落点模式（只作用于无门路径：BeginRun 开局 / RequestDirectSwitch 直达）。
    /// 门演出路径的落点仍由 PortalDoor 把玩家相对出口锚点的位姿映射到目标关 Entry 锚点，与此无关。</summary>
    public enum PlayerLanding
    {
        /// <summary>保持原位不动（旧行为；直达路径默认，需要落点请显式选 LevelSpawnPoint）。</summary>
        KeepCurrent,
        /// <summary>落到目标关的默认出生点（PlayerSpawnPoint；目标关没摆标记 → 保持原位并留日志）。</summary>
        LevelSpawnPoint,
    }

    /// <summary>把玩家放到 levelScene 的默认出生点（landing = LevelSpawnPoint 时）。
    /// 无标记 / 无玩家 / 无控制器 → 记录日志并保持原位（向后兼容：老关没摆出生点 = 旧行为），绝不抛错。
    /// 传送先于 SetSettled 执行 —— Settled 后的自动存档必然采到出生点位姿。</summary>
    private void PlacePlayerAtSpawn(Scene levelScene, PlayerLanding landing)
    {
        if (landing == PlayerLanding.KeepCurrent) return;

        var spawn = PlayerSpawnPoint.FindDefault(levelScene);
        if (spawn == null)
        {
            Debug.Log($"[LevelTransitionManager] {levelScene.name} 没有出生点(PlayerSpawnPoint)，玩家保持原位", this);
            return;
        }

        var player = GameObject.FindGameObjectWithTag("Player");
        if (player == null)
        {
            Debug.LogWarning("[LevelTransitionManager] 找不到玩家（tag \"Player\"），跳过出生点落位", this);
            return;
        }
        var flight = player.GetComponent<BeeFlightController>();
        if (flight == null)
        {
            Debug.LogWarning("[LevelTransitionManager] 玩家缺少 BeeFlightController，跳过出生点落位", this);
            return;
        }

        flight.TeleportTo(spawn.transform.position, spawn.transform.eulerAngles.y);
        Debug.Log($"[LevelTransitionManager] 玩家已放到 {levelScene.name} 出生点 {spawn.name}", this);
    }

    // ==================== 通用请求区（封装层：刷卡门 / 场景组件 / 内容的唯一公开入口） ====================

    /// <summary>
    /// 出口门刷卡（门演出路径, T0）：读卡器读到了本关出口门的钥匙（CardReader 调用,
    /// 归属校验已在读卡器侧按门完成）。目的地唯一权威 = 卡 —— 刷卡瞬间从卡读取
    /// 目的地并加载（最近一次刷卡生效）。过渡未穿过前再刷另一张卡 = 换目标:
    /// 先关门上锁 → 停/卸已载目标 → 载新目标 → 开门放行。
    /// 门的开/关时机全部由管理器控制（加载完成才开门）。
    /// 只允许在稳定点刷卡；恢复 / 卸载期一律拒绝（卸载窗口的刷卡会挂起续传）。
    /// </summary>
    public bool RequestExitKeyed(SlidingDoor swipedDoor) =>
        RequestExitKeyedCore(swipedDoor, null);   // 旧调用不带卡:出口门无目的地来源 → 报错拒绝

    /// <summary>出口门刷卡 + 钥匙（CardReader 用;目的地从这张卡读取）。</summary>
    public bool RequestExitKeyed(SlidingDoor swipedDoor, IDoorKey key) =>
        RequestExitKeyedCore(swipedDoor, key);

    private bool RequestExitKeyedCore(SlidingDoor swipedDoor, IDoorKey key)
    {
        if (swipedDoor == null) return false;

        ExitPortal portal = null;
        foreach (var e in exitPortals)
            if (e.door == swipedDoor) { portal = e; break; }
        if (portal == null)
        {
            Debug.Log("[LevelTransitionManager] 刷的不是当前关的出口门，忽略", this);
            return false;
        }

        // 刷卡瞬间裁决目的地(唯一权威 = 卡;卡没配/配错 → 报错拒绝,不做兜底)
        if (!TryResolveCardDestination(key, out var dest))
            return false;

        if (transitionTriggered)
        {
            // 门未穿过 → 允许换卡换目的地;已穿过(结算完) → 单向门拒绝
            if (passFinalized)
            {
                Debug.Log("[LevelTransitionManager] 本次过渡已穿过结算，拒绝重复刷卡（单向门）", this);
                return false;
            }
            if (portal != activePortal)
            {
                Debug.Log("[LevelTransitionManager] 已有其他出口门在过渡中，只允许重刷当前这道门", this);
                return false;
            }
            Debug.Log($"[LevelTransitionManager] 换卡重刷：目的地改为 kind={dest.kind}", this);
            StartCoroutine(RetargetRoutine(portal, dest));
            return true;
        }

        // 卸载上一关窗口内刷了刚进入的这关的门：挂起(同存目的地),卸载完成后自动续传
        if (unloading)
        {
            pendingPortal = portal;
            pendingDestination = dest;
            transitionTriggered = true;
            Debug.Log("[LevelTransitionManager] 正在卸载上一关，刷卡挂起，卸载完成后继续", this);
            return true;
        }

        if (!settled)
        {
            Debug.Log("[LevelTransitionManager] 关卡尚未进入稳定点（开局/读档恢复中），拒绝刷卡", this);
            return false;
        }

        AdvanceTransition(portal, dest);
        return true;
    }

    /// <summary>从刷卡钥匙裁决目的地：只认 Card 携带的目的地;没配/缺目标 → 报错并返回 false。</summary>
    private bool TryResolveCardDestination(IDoorKey key, out ResolvedDestination dest)
    {
        dest = default;
        var card = key as Card;
        if (card == null)
        {
            Debug.LogError("[LevelTransitionManager] 刷卡钥匙没有目的地(出口门的目的地唯一权威是卡:请用带 Card 的刷卡,并在卡上配置 Destination Kind)。刷卡被拒", this);
            return false;
        }
        string hint = card.DiagnosticHint;
        if (!string.IsNullOrEmpty(hint))
        {
            Debug.LogError(hint + " —— 刷卡被拒", this);
            return false;
        }
        dest = new ResolvedDestination(card.DestinationKind, card.DestinationScenePath, card.DestinationEndingId);
        return true;
    }

    /// <summary>（旧名保留：等价于 RequestExitKeyed。仅兼容旧调用点，新代码请用 RequestExitKeyed。）</summary>
    public bool OnExitDoorKeyed(SlidingDoor swipedDoor) => RequestExitKeyed(swipedDoor);

    /// <summary>（更早的旧名保留：等价于 RequestExitKeyed。）</summary>
    public bool OnCardSwiped(SlidingDoor swipedDoor) => RequestExitKeyed(swipedDoor);

    /// <summary>开始一道出口门的过渡（目的地已由刷卡瞬间裁决）。</summary>
    private void AdvanceTransition(ExitPortal portal, ResolvedDestination dest)
    {
        GameEvents.TransitionStart?.Invoke();   // 过渡开始音效(注册式同步)

        activePortal = portal;
        transitionTriggered = true;
        passFinalized = false;                  // 新一轮过渡：重置上次过渡的结算状态
        nextScene = default;

        if (dest.kind == LevelAnchor.DestinationKind.Ending)
        {
            // 结局门：不加载关卡，通知流程进入结局（流程负责淡出 / 收局 / Room_00 演出）。
            Debug.Log($"[LevelTransitionManager] 结局卡被刷卡：触发结局 [{dest.endingId}]");
            loadState = LoadState.Done;
            EndingRequested?.Invoke(dest.endingId);
            return;
        }

        // 加载型目的地只剩 Scene(卡上显式指定的目标场景;没配目标的卡在 TryResolveCardDestination
        // 已被拒绝)。非线性关卡图没有"默认下一关"可退,这条路径必须显式。
        string destPath = dest.scenePath;

        if (string.IsNullOrEmpty(destPath))
        {
            Debug.LogError($"[LevelTransitionManager] 刷卡目的地场景为空，刷卡无效（卡 {dest.kind}）", this);
            transitionTriggered = false;
            return;
        }
        if (destPath == currentScene.path)
        {
            // 自环 = 误配：目标就是本关。当前关已在场景里加载着，再加载一次会得到重复的关卡实例。
            // （关卡图可以有回环 —— A→B→A 合法，因为 A 在前一站已被卸载；这里挡的只是"去自己"。）
            Debug.LogError($"[LevelTransitionManager] 刷卡目的地与本关相同（自环），刷卡无效", this);
            transitionTriggered = false;
            return;
        }

        // T0：只启动后台加载。门保持关闭且锁定 —— 加载完成前玩家不可能通过，
        //     门打开时目的地关必然已经加载完成（见 LoadNextAndOpen）。
        activeDestPath = destPath;
        loadState = LoadState.Loading;
        SetSettled(false);
        Debug.Log($"[LevelTransitionManager] T0: 刷卡成功，后台加载目的地 {destPath}（门保持关闭）", this);
        loadRoutine = StartCoroutine(LoadNextAndOpen(destPath, portal));
    }

    // ==================== T1：加载 + 开门 ====================

    private IEnumerator LoadNextAndOpen(string destPath, ExitPortal portal)
    {
        int gen = loadGeneration;   // 本趟加载的代际：换卡重刷(RetargetRoutine)把代际自增后，本趟在自检点退出
        float startTime = Time.time;   // T0 时刻：用于让"门保持关闭"阶段可见
        try
        {
            var op = SceneManager.LoadSceneAsync(destPath, LoadSceneMode.Additive);
            loadOp = op;
            if (op == null)
            {
                Debug.LogError($"[LevelTransitionManager] 场景加载失败（op 为 null）：{destPath}", this);
                loadState = LoadState.Idle;
                if (gen == loadGeneration) SetSettled(true);
                yield break;
            }

            // 先加载内容、不激活：保证场景激活时已完成全部准备，玩家看不到"未就位画面"
            op.allowSceneActivation = false;
            while (op.progress < 0.9f)
                yield return null;

            if (simulatedLoadDelay > 0f)
                yield return new WaitForSeconds(simulatedLoadDelay);   // 仅测试：模拟慢加载

            // 换卡重刷会先放行旧加载（allowSceneActivation = true）让本趟尽快收尾
            op.allowSceneActivation = true;
            yield return op;

            // 自检点：本趟已被换卡重刷顶掉 → 直接退场，卸载由 RetargetRoutine 负责
            if (gen != loadGeneration) yield break;

            // T1：加载完成 → 激活新场景并解析入口锚点（传送门位姿基准）→ 解锁开门。
            //     门打开时目的地关已完整就位：玩家看到门后画面的瞬间它已存在，无任何加载痕迹。
            nextScene = SceneManager.GetSceneByPath(destPath);
            SceneManager.SetActiveScene(nextScene);
            entryAnchor = LevelAnchor.FindAnchor(nextScene, LevelAnchor.AnchorType.Entry);
            if (entryAnchor == null)
                Debug.LogError($"[LevelTransitionManager] 目的地 {nextScene.name} 缺少入口锚点（LevelAnchor Entry），传送门无法工作", this);
            loadState = LoadState.Done;

            // 顺序保证：加载已在上方完成；门再等满 minDoorCloseTime 才打开，
            // 让"拾卡 → 门保持关闭 → 加载 → 开门"的顺序可见（加载瞬间完成时也先关够时间）。
            float remaining = minDoorCloseTime - (Time.time - startTime);
            if (remaining > 0f)
                yield return new WaitForSeconds(remaining);

            if (gen != loadGeneration) yield break;   // 等门期间被换卡：不再开门/激活

            portal.door.SetLocked(false);
            portal.door.OpenDoor();
            // 传送门与门同步激活：门开瞬间门面即显示目的地关实时画面
            if (portal.portal != null && entryAnchor != null)
                portal.portal.Activate(entryAnchor);

            Debug.Log($"[LevelTransitionManager] T1: 目的地 {nextScene.name} 加载完成，门开启（刷卡到开门共 {Time.time - startTime:F2}s）", this);
        }
        finally
        {
            loadRoutine = null;
            loadOp = null;
        }
    }

    /// <summary>
    /// 换卡重刷：门已触发过渡但未穿过，再刷另一张卡 → 按用户要求顺序换目标：
    /// 先关门上锁（门 = 屏障）→ 让旧加载收尾并卸载旧目标 → 载新目标（重走 T0→T1）。
    /// </summary>
    private IEnumerator RetargetRoutine(ExitPortal portal, ResolvedDestination dest)
    {
        // 1. 先关门上锁：门是物理屏障，旧目标卸载后玩家也穿不过去
        portal.door.SetLocked(true);
        portal.door.CloseDoor();
        PortalPoseSO.Instance?.Deactivate();   // 门面显示停(旧目标画面作废)

        // 2. 顶掉旧加载：放行旧异步加载 → 旧协程在自检点(gen 不符)退出；等它完全退场
        loadGeneration++;
        if (loadOp != null && !loadOp.isDone)
            loadOp.allowSceneActivation = true;
        while (loadRoutine != null)
            yield return null;

        // 3. 卸载旧目标（若已加载进来；activeDestPath 为本趟目标）
        if (!string.IsNullOrEmpty(activeDestPath))
        {
            var oldScene = SceneManager.GetSceneByPath(activeDestPath);
            if (oldScene.IsValid() && oldScene.isLoaded)
            {
                var uop = SceneManager.UnloadSceneAsync(oldScene);
                if (uop != null) yield return uop;
            }
        }
        Resources.UnloadUnusedAssets();

        // 4. 载新目标（重走 T0→T1:加载 → 开门 → 门面复活）
        AdvanceTransition(portal, dest);
    }

    // ==================== T2：穿过结算 ====================

    /// <summary>T2：玩家穿过传送门（PortalDoor 已把玩家传送到目的地关入口门洞）。</summary>
    public void OnPortalCrossed(PortalDoor sender)
    {
        if (passFinalized) return;   // 防重复结算

        passFinalized = true;
        transitionTriggered = false; // 新关卡需要在新关出口读卡器上重新刷卡
        GameEvents.LevelConfirm?.Invoke();   // 关卡切换确认音效(注册式同步)

        // 旧关信息留给卸载使用
        prevScene = currentScene;

        // 切到新关卡（身份 = 场景路径；注册表序数随路径反查）
        currentScene = nextScene;
        currentLevelIndex = IndexOfPath(nextScene.path);
        nextScene = default;
        activePortal = null;
        activeDestPath = null;
        Debug.Log($"[LevelTransitionManager] T2: 传送穿过，当前关卡 = {currentScene.name}", this);

        // 解析新关出口门并预锁（下一段过渡的门口）
        ResolveExitsForCurrentLevel();

        // T3：立即完全卸载前场景（门是单向的：前场景已卸载，无法返回）
        StartCoroutine(UnloadPrevLevel());
    }

    // ==================== T3：卸载 ====================

    private IEnumerator UnloadPrevLevel()
    {
        GameEvents.UnloadFade?.Invoke();   // 卸载淡出音效(注册式同步)
        unloading = true;
        Debug.Log("[LevelTransitionManager] T3: 直接卸载上一关", this);

        // 卸载前一关（异步，后台执行，不影响玩家操作）
        var op = SceneManager.UnloadSceneAsync(prevScene);
        yield return op;

        // 清理未使用资源（异步执行，不等待）
        Resources.UnloadUnusedAssets();

        prevScene = default;
        unloading = false;
        Debug.Log("[LevelTransitionManager] 上一关已卸载并清理资源", this);

        // 卸载期间刷了新关的门 → 现在续传过渡(目的地 = 刷卡瞬间已裁决的结果)
        if (pendingPortal != null)
        {
            var portal = pendingPortal;
            pendingPortal = null;
            var dest = pendingDestination;
            pendingDestination = default;
            AdvanceTransition(portal, dest);
        }
        else
        {
            SetSettled(true);   // 到达新关并站稳：自动存档等挂在 Settled 事件上
        }
    }

    // ==================== 通用请求区：无门直达（RequestDirectSwitch） ====================

    /// <summary>
    /// 直达换场景（无门演出;选关 / 调试 / 彩蛋传送用）：
    /// 只做"装卸关卡场景 + 切活动场景 + 新关出口预锁",不碰钥匙与传送门。
    /// 玩家场景不动;玩家位移由 landing 决定:本签名 = 玩家原地不动(旧语义,原样保留),
    /// 需要落点请用带 PlayerLanding 的重载(落到目标关默认出生点)。
    /// 守卫:仅稳定点 + 无进行中的门过渡 + 目标必须在关卡注册表内(玩家 / 背景
    /// Room_00 等非关卡场景由列表天然排除),且 ≠ 当前关。
    /// 完成后 SetSettled(true) → Settled 事件 → 流程"到达自动存档"自动生效。
    /// </summary>
    public bool RequestDirectSwitch(string scenePath) =>
        DirectSwitchRequested(scenePath, PlayerLanding.KeepCurrent);

    /// <summary>
    /// 直达换场景 + 落点（到达后按 landing 决定玩家去留;与单参版本同守卫同时序）。
    /// landing = LevelSpawnPoint:加载完成后把玩家放到目标关默认出生点(PlayerSpawnPoint;
    /// 目标关没摆标记 → 保持原位并留日志)。落点先于 SetSettled —— 自动存档采到出生点位姿。
    /// </summary>
    public bool RequestDirectSwitch(string scenePath, PlayerLanding landing) =>
        DirectSwitchRequested(scenePath, landing);

    private bool DirectSwitchRequested(string scenePath, PlayerLanding landing)
    {
        if (!settled)
        {
            Debug.LogWarning("[LevelTransitionManager] 直达请求被拒：当前不在稳定点（加载/过渡/恢复中）", this);
            return false;
        }
        if (busyActive || transitionTriggered)
        {
            Debug.LogWarning("[LevelTransitionManager] 直达请求被拒：有开局/收局/门过渡流程进行中", this);
            return false;
        }
        if (!currentScene.IsValid() || !currentScene.isLoaded)
        {
            Debug.LogWarning("[LevelTransitionManager] 直达请求被拒：当前没有已加载的关卡（直达只用于游玩中）", this);
            return false;
        }
        if (string.IsNullOrEmpty(scenePath))
        {
            Debug.LogWarning("[LevelTransitionManager] 直达请求被拒：目标场景路径为空", this);
            return false;
        }
        if (scenePath == currentScene.path)
        {
            Debug.LogWarning("[LevelTransitionManager] 直达请求被拒：目标与当前关相同（自环）", this);
            return false;
        }
        if (IndexOfPath(scenePath) < 0)
        {
            Debug.LogWarning($"[LevelTransitionManager] 直达请求被拒：{scenePath} 不在关卡注册表内（该加载哪一个由列表决定）", this);
            return false;
        }
        if (scenePath == playerScenePath || scenePath.EndsWith("/" + SaveSchema.BackdropSceneFileName))
        {
            Debug.LogWarning($"[LevelTransitionManager] 直达请求被拒：{scenePath} 是玩家/背景场景", this);
            return false;
        }

        StartCoroutine(DirectSwitchCoroutine(scenePath, landing));
        return true;
    }

    private IEnumerator DirectSwitchCoroutine(string scenePath, PlayerLanding landing)
    {
        Scene prevSceneLocal = currentScene;
        SetSettled(false);
        Debug.Log($"[LevelTransitionManager] 直达换场景：{currentScene.name} → {scenePath}（无门演出，落点 = {landing}）", this);

        // 加载目标关(激活 / 当前关身份均在此完成;失败时 currentScene 保持原关)
        yield return LoadLevelByPathCoroutine(scenePath);
        if (!currentScene.IsValid() || !currentScene.isLoaded || currentScene.path != scenePath)
        {
            Debug.LogError($"[LevelTransitionManager] 直达失败：{scenePath} 未能加载，停留原关", this);
            SetSettled(prevSceneLocal.IsValid() && prevSceneLocal.isLoaded);
            yield break;
        }

        // 卸载原关(原关只在直达成功后卸载 —— 失败时原关仍在,可继续游玩;
        // 卸载协程天然跨帧,新场景组件 Start 已跑完,落点无需再等帧)
        if (prevSceneLocal.IsValid() && prevSceneLocal.isLoaded)
        {
            var op = SceneManager.UnloadSceneAsync(prevSceneLocal);
            if (op != null) yield return op;
        }
        Resources.UnloadUnusedAssets();

        ResolveExitsForCurrentLevel();   // 新关出口预锁(下一段过渡的门口)
        PlacePlayerAtSpawn(currentScene, landing);   // 落点(目标关出生点 / 保持原位)—— 先于 Settled
        Debug.Log($"[LevelTransitionManager] 直达完成：{currentScene.name} 出口已就绪", this);
        SetSettled(true);   // → Settled(流程的"到达自动存档")
    }

    // ==================== 通用：加载一个关卡 ====================

    /// <summary>按注册表序加载关卡（开局用；序数写入 currentLevelIndex 并设活动场景）。</summary>
    private IEnumerator LoadLevelByIndexCoroutine(int index)
    {
        if (index < 0 || index >= levelPaths.Length) yield break;
        yield return LoadLevelByPathCoroutine(levelPaths[index]);
        currentLevelIndex = index;
    }

    /// <summary>按路径加载关卡（开局 / 读档共用）：激活、写当前场景。</summary>
    private IEnumerator LoadLevelByPathCoroutine(string path)
    {
        var op = SceneManager.LoadSceneAsync(path, LoadSceneMode.Additive);
        if (op == null)
        {
            Debug.LogError($"[LevelTransitionManager] 场景加载失败（op 为 null）：{path}", this);
            yield break;
        }
        yield return op;
        currentScene = SceneManager.GetSceneByPath(path);
        currentLevelIndex = IndexOfPath(path);
        loadState = LoadState.Done;
        transitionTriggered = false;
        SceneManager.SetActiveScene(currentScene);
    }

    /// <summary>场景路径在注册表中的序数（不在注册表 = -1，如分支目的地 / 旧存档指向已移除的关）。</summary>
    private int IndexOfPath(string path)
    {
        if (levelPaths != null)
            for (int i = 0; i < levelPaths.Length; i++)
                if (levelPaths[i] == path) return i;
        return -1;
    }

    private void SetSettled(bool value)
    {
        if (settled == value) return;
        settled = value;
        if (value)
        {
            Settled?.Invoke();
        }
    }
}
