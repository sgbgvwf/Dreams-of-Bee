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
///   - 关卡注册表 = Inspector 的 levels 列表(顺序即线性默认序),失效时从 Build Settings 推导
///     (自动跳过本场景 / 玩家场景 / 菜单背景 Room_00);
///   - 每道出口门(出口锚点 LevelAnchor,一个场景可有多道)各自声明目的地:
///     LinearNext(列表下一关,默认)/ Scene(任意关卡) / Ending(结局);
///   - 当前关身份 = 场景路径(存档 / 读档的事实来源),currentLevelIndex 只是线性序数(展示 / 默认序用)。
///
/// 运行生命周期(由 GameFlowManager 驱动):
///   - BeginRun(i)   : 开局(菜单→新游戏):并行加载玩家场景 + 第 i 关,锁好出口门后 Settled;
///   - BeginResume(path): 读档:加载玩家场景 + 指定关卡(不 Settled —— 等流程恢复完场景物件与玩家,
///                     由流程调 SettleAfterRestore());
///   - EndRunToMenuCoroutine(): 收局:卸载关卡与玩家场景、清理资源、复位内部状态。
///   - Settled / IsSettled: 无任何加载 / 卸载 / 待穿越过渡的稳定点。刷卡、存档都只在 Settled 允许;
///     每次"到达新关站稳"(Begin* 完成 / T2 穿越 + T3 卸载完成)会触发 Settled 事件 → 流程自动存档。
///
/// 通用请求区(封装层 —— 一切"转换到别的场景"的公开入口,见 Request* 方法):
///   - RequestExitKeyed(door):刷卡门(门演出路径,T0–T3,原地保留);
///   - RequestDirectSwitch(path):无门直达换场景(无演出;调试 / 演示 / 选关用);
///   - 结局 / 主菜单请求跨流程状态与存档收局,归 GameFlowManager(TriggerEnding / QuitToMenu),
///     刷卡门"结局门"仍经 EndingRequested 事件交给流程;
///   - 本层只负责"当前是哪个关卡、何时加载、加载哪一个";玩家落点与钥匙语义不属于本层
///     (门演出路径的落点由 PortalDoor 负责;直达路径的落点由调用方自理)。
/// 每场景配套的薄 Facade 组件见 SceneTransition.cs(场景 UI 按钮经它调用上述请求)。
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
/// 新增一关(单向关卡图)全流程:
///   1) 新建场景并摆内容; 2) 加入 Build Settings 并拖进本管理器 Inspector 的 Levels 列表(顺序=线性默认序);
///   3) 场景入口门洞放 Entry 锚点; 4) 出口门洞放 Exit 锚点(挂 SlidingDoor 门引用 + PortalDoor);
///   5) 出口门 SlidingDoor.LevelIndex = 该关在列表的序号(与卡片配对); 6) 刷卡通过 → 默认去线性下一关,
///      想分支就在 Exit 锚点上改 DestinationKind / 拖目标场景 / 填结局 id。核心零改动。
/// </summary>
public class LevelTransitionManager : MonoBehaviour
{
    public static LevelTransitionManager Instance { get; private set; }

    /// <summary>当前关卡在线性列表中的序数（-1 = 当前关不在列表，如分支目的地；展示 / 默认序用）。</summary>
    public int CurrentLevelIndex => currentLevelIndex;

    /// <summary>当前关卡场景路径（存档 / 读档的唯一事实来源；空 = 无当前关卡）。</summary>
    public string CurrentLevelPath => currentScene.IsValid() && currentScene.isLoaded ? currentScene.path : "";

    /// <summary>当前关卡场景（SaveSystem 采集场景物件状态用）。</summary>
    public Scene CurrentLevelScene => currentScene;

    /// <summary>关卡注册表长度（Inspector levels 列表序；场景组件 / UI / 内容读取用）。</summary>
    public int LevelCount => levelPaths != null ? levelPaths.Length : 0;

    /// <summary>关卡注册表第 index 关的场景路径（越界返回空串）。</summary>
    public string GetLevelPath(int index) =>
        levelPaths != null && index >= 0 && index < levelPaths.Length ? levelPaths[index] : "";

    /// <summary>场景路径在关卡注册表中的序数（不在列表 = -1，如分支目的地）。</summary>
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

    /// <summary>进入稳定点时触发（Begin* 完成 / T2+T3 穿越结算完成）。GameFlowManager 借此做"到达自动存档"。</summary>
    public event System.Action Settled;

    /// <summary>结局门（出口目的地 = Ending）被刷卡时触发（载荷：结局 id）。无监听者（开发者直玩等）则只开门不换场。</summary>
    public event System.Action<string> EndingRequested;

    // === 一口出口的运行时配置（一个出口锚点 = 一扇门 + 一个可选的传送门 + 一个目的地） ===
    private sealed class ExitPortal
    {
        public LevelAnchor anchor;      // 出口锚点（目的地配置所在）
        public SlidingDoor door;        // 出口门
        public PortalDoor portal;       // 出口锚点上的传送门（T1 激活；可空）
    }

#if UNITY_EDITOR
    [SerializeField, Tooltip("关卡列表：Inspector 按顺序拖入（不含 Persistance 启动场景与 Room_00 背景）")]
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

    private int currentLevelIndex = -1;         // 当前关在线性列表的序数（-1 = 不在列表）
    private LoadState loadState = LoadState.Idle;       // 加载状态
    private bool unloading;                     // 是否正在卸载（防重复卸载；卸载期间刷卡 → 挂起）
    private bool passFinalized;                 // 本次过渡是否已结算（防重复结算）
    private bool transitionTriggered;           // 本关是否已刷卡触发过渡（防重复触发）
    private bool settled;                       // 稳定点（无任何加载 / 卸载 / 待穿越过渡）
    private bool busyActive;                    // 生命周期协程(Begin*/EndRun)占用中,防重入
    private ExitPortal pendingPortal;           // 卸载期间刷的新出口门 → 卸载完成后续传

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

    /// <summary>开局：加载玩家场景 + 线性第 startLevelIndex 关，锁好出口后进入稳定点。</summary>
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

    /// <summary>收局（返回主菜单 / 结局）：卸载当前关与玩家场景、清理资源、复位内部状态。流程以协程方式运行本方法。</summary>
    public IEnumerator EndRunToMenuCoroutine()
    {
        busyActive = true;
        SetSettled(false);

        // 卸载当前关卡（若有）
        if (currentScene.IsValid() && currentScene.isLoaded)
        {
            var op = SceneManager.UnloadSceneAsync(currentScene);
            if (op != null) yield return op;
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
        int linearIndex = IndexOfPath(currentScene.path);

        foreach (var anchor in LevelAnchor.FindAllAnchors(currentScene, LevelAnchor.AnchorType.Exit))
        {
            var door = anchor.ResolveExitDoor();

            // 关卡识别：出口门必须声明它属于本关（防止误配）。仅当门与当前关都在线性列表内时校验
            // —— 分支目的地关不在列表（序数 -1）时跳过校验，钥匙归属由读卡器侧按门校验。
            if (door != null && door.LevelIndex >= 0 && linearIndex >= 0 && door.LevelIndex != linearIndex)
            {
                Debug.LogError($"[LevelTransitionManager] {currentScene.name} 的出口门归属关卡({door.LevelIndex})与本关线性序({linearIndex})不符，忽略该门", this);
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

    // ==================== 通用请求区（封装层：刷卡门 / 场景组件 / 内容的唯一公开入口） ====================

    /// <summary>
    /// 出口门刷卡（门演出路径, T0）：读卡器读到了本关任意出口门的钥匙
    /// （CardReader 调用,归属校验已在读卡器侧按门完成;场景组件与内容也可直接调）。
    /// 刷卡只负责触发过渡；门的开/关时机全部由管理器控制（加载完成才开门）。
    /// 只允许在稳定点刷卡；恢复 / 加载 / 卸载期一律拒绝（卸载窗口的刷卡会挂起续传）。
    /// </summary>
    public bool RequestExitKeyed(SlidingDoor swipedDoor)
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

        if (transitionTriggered)
        {
            Debug.Log("[LevelTransitionManager] 已触发过过渡，忽略重复刷卡", this);
            return false;
        }

        // 卸载上一关窗口内刷了刚进入的这关的门：挂起，卸载完成后自动续传（防"卸载中加载"状态冲突）
        if (unloading)
        {
            pendingPortal = portal;
            transitionTriggered = true;
            Debug.Log("[LevelTransitionManager] 正在卸载上一关，刷卡挂起，卸载完成后继续", this);
            return true;
        }

        if (!settled)
        {
            Debug.Log("[LevelTransitionManager] 关卡尚未进入稳定点（开局/读档恢复中），拒绝刷卡", this);
            return false;
        }

        AdvanceTransition(portal);
        return true;
    }

    /// <summary>（旧名保留：等价于 RequestExitKeyed。仅兼容旧调用点，新代码请用 RequestExitKeyed。）</summary>
    public bool OnExitDoorKeyed(SlidingDoor swipedDoor) => RequestExitKeyed(swipedDoor);

    /// <summary>（更早的旧名保留：等价于 RequestExitKeyed。）</summary>
    public bool OnCardSwiped(SlidingDoor swipedDoor) => RequestExitKeyed(swipedDoor);

    /// <summary>开始一道出口门的过渡（防重闸已在调用方完成）。</summary>
    private void AdvanceTransition(ExitPortal portal)
    {
        GameEvents.TransitionStart?.Invoke();   // 过渡开始音效(注册式同步)

        activePortal = portal;
        transitionTriggered = true;
        passFinalized = false;                  // 新一轮过渡：重置上次过渡的结算状态
        nextScene = default;

        var kind = portal.anchor.Destination;
        bool noLinearNext = currentLevelIndex < 0 || currentLevelIndex + 1 >= levelPaths.Length;
        if (kind == LevelAnchor.DestinationKind.LinearNext && noLinearNext)
        {
            // 线性序列的最后一关(或当前关不在列表 —— 分支目的地未配出口时误配兜底)：
            // 没有下一关可加载，直接解锁开门（可作关卡终点门）。
            // 稳定点保持 —— 玩家可正常存档 / 返回菜单；这道门已被消费，不会二次触发。
            Debug.Log($"[LevelTransitionManager] 已是线性最后一关(序数 {currentLevelIndex})，没有下一关可加载；如需去往其他关/结局，请在出口锚点上配置 Destination");
            loadState = LoadState.Done;
            portal.door.SetLocked(false);
            portal.door.OpenDoor();
            return;
        }

        if (kind == LevelAnchor.DestinationKind.Ending)
        {
            // 结局门：不加载关卡，通知流程进入结局（流程负责淡出 / 收局 / Room_00 演出）。
            Debug.Log($"[LevelTransitionManager] 结局门被刷卡：触发结局 [{portal.anchor.EndingId}]");
            loadState = LoadState.Done;
            EndingRequested?.Invoke(portal.anchor.EndingId);
            return;
        }

        string destPath = kind == LevelAnchor.DestinationKind.Scene
            ? portal.anchor.DestinationScenePath
            : levelPaths[currentLevelIndex + 1];

        if (string.IsNullOrEmpty(destPath))
        {
            Debug.LogError($"[LevelTransitionManager] 出口 {portal.anchor.name} 的目的地为空，刷卡无效", this);
            transitionTriggered = false;
            return;
        }
        if (destPath == currentScene.path)
        {
            // 单向约束：目标是本关自身视为误配（框架不做回访 / 重入）
            Debug.LogError($"[LevelTransitionManager] 出口 {portal.anchor.name} 的目标与本关相同，单向框架禁止回访，刷卡无效", this);
            transitionTriggered = false;
            return;
        }

        // T0：只启动后台加载。门保持关闭且锁定 —— 加载完成前玩家不可能通过，
        //     门打开时目的地关必然已经加载完成（见 LoadNextAndOpen）。
        loadState = LoadState.Loading;
        SetSettled(false);
        Debug.Log($"[LevelTransitionManager] T0: 刷卡成功，后台加载目的地 {destPath}（门保持关闭）", this);
        StartCoroutine(LoadNextAndOpen(destPath, portal));
    }

    // ==================== T1：加载 + 开门 ====================

    private IEnumerator LoadNextAndOpen(string destPath, ExitPortal portal)
    {
        float startTime = Time.time;   // T0 时刻：用于让"门保持关闭"阶段可见
        var op = SceneManager.LoadSceneAsync(destPath, LoadSceneMode.Additive);
        if (op == null)
        {
            Debug.LogError($"[LevelTransitionManager] 场景加载失败（op 为 null）：{destPath}", this);
            loadState = LoadState.Idle;
            SetSettled(true);
            yield break;
        }

        // 先加载内容、不激活：保证场景激活时已完成全部准备，玩家看不到"未就位画面"
        op.allowSceneActivation = false;
        while (op.progress < 0.9f)
            yield return null;

        if (simulatedLoadDelay > 0f)
            yield return new WaitForSeconds(simulatedLoadDelay);   // 仅测试：模拟慢加载

        op.allowSceneActivation = true;
        yield return op;

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

        portal.door.SetLocked(false);
        portal.door.OpenDoor();
        // 传送门与门同步激活：门开瞬间门面即显示目的地关实时画面
        if (portal.portal != null && entryAnchor != null)
            portal.portal.Activate(entryAnchor);
        GameEvents.LevelReady?.Invoke(nextScene);   // 门开瞬间同步切换新关环境音(注册式同步)

        Debug.Log($"[LevelTransitionManager] T1: 目的地 {nextScene.name} 加载完成，门开启（刷卡到开门共 {Time.time - startTime:F2}s）", this);
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

        // 切到新关卡（身份 = 场景路径；线性序数随路径反查）
        currentScene = nextScene;
        currentLevelIndex = IndexOfPath(nextScene.path);
        nextScene = default;
        activePortal = null;
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

        // 卸载期间刷了新关的门 → 现在续传过渡
        if (pendingPortal != null)
        {
            var portal = pendingPortal;
            pendingPortal = null;
            AdvanceTransition(portal);
        }
        else
        {
            SetSettled(true);   // 到达新关并站稳：自动存档等挂在 Settled 事件上
        }
    }

    // ==================== 通用请求区：无门直达（RequestDirectSwitch） ====================

    /// <summary>
    /// 直达换场景（无门演出;线性直达 / 选关 / 调试 / 彩蛋传送用）：
    /// 只做"装卸关卡场景 + 切活动场景 + 新关出口预锁",不碰钥匙与传送门,
    /// 玩家场景不动、玩家不做位移 —— 落点是调用方职责(需要时自行摆位或用
    /// BeeFlightController.ApplyPortalTransform)。
    /// 守卫:仅稳定点 + 无进行中的门过渡 + 目标必须在关卡注册表内(玩家 / 背景
    /// Room_00 等非关卡场景由列表天然排除),且 ≠ 当前关。
    /// 完成后 SetSettled(true) → Settled 事件 → 流程"到达自动存档"自动生效。
    /// </summary>
    public bool RequestDirectSwitch(string scenePath)
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
            Debug.LogWarning("[LevelTransitionManager] 直达请求被拒：目标与当前关相同（单向框架禁止回访）", this);
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

        StartCoroutine(DirectSwitchCoroutine(scenePath));
        return true;
    }

    private IEnumerator DirectSwitchCoroutine(string scenePath)
    {
        Scene prevSceneLocal = currentScene;
        SetSettled(false);
        Debug.Log($"[LevelTransitionManager] 直达换场景：{currentScene.name} → {scenePath}（无门演出）", this);

        // 加载目标关(激活 / 当前关身份 / LevelReady 均在此完成;失败时 currentScene 保持原关)
        yield return LoadLevelByPathCoroutine(scenePath);
        if (!currentScene.IsValid() || !currentScene.isLoaded || currentScene.path != scenePath)
        {
            Debug.LogError($"[LevelTransitionManager] 直达失败：{scenePath} 未能加载，停留原关", this);
            SetSettled(prevSceneLocal.IsValid() && prevSceneLocal.isLoaded);
            yield break;
        }

        // 卸载原关(原关只在直达成功后卸载 —— 失败时原关仍在,可继续游玩)
        if (prevSceneLocal.IsValid() && prevSceneLocal.isLoaded)
        {
            var op = SceneManager.UnloadSceneAsync(prevSceneLocal);
            if (op != null) yield return op;
        }
        Resources.UnloadUnusedAssets();

        ResolveExitsForCurrentLevel();   // 新关出口预锁(下一段过渡的门口)
        Debug.Log($"[LevelTransitionManager] 直达完成：{currentScene.name} 出口已就绪", this);
        SetSettled(true);   // → Settled(流程的"到达自动存档")
    }

    // ==================== 通用：加载一个关卡 ====================

    /// <summary>按线性序加载关卡（开局用；序数写入 currentLevelIndex 并设活动场景）。</summary>
    private IEnumerator LoadLevelByIndexCoroutine(int index)
    {
        if (index < 0 || index >= levelPaths.Length) yield break;
        yield return LoadLevelByPathCoroutine(levelPaths[index]);
        currentLevelIndex = index;
    }

    /// <summary>按路径加载关卡（开局 / 读档共用）：激活、写当前场景、播 LevelReady。</summary>
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
        GameEvents.LevelReady?.Invoke(currentScene);   // 加载完成:开当前关卡环境音(注册式同步)
    }

    /// <summary>场景路径在线性列表中的序数（不在列表 = -1，如分支目的地 / 旧存档指向已移除的关）。</summary>
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
