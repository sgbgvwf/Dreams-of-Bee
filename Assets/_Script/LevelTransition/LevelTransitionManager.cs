using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// 关卡切换管理器（单例）：无缝关卡推进的核心状态机。挂在 Persistance 启动场景的 LevelManager 上。
///
/// 启动引导：并行加载玩家场景（Player.unity）与第 0 关。玩家有独立的常驻场景 —— 与 Persistance
/// 相同的机制：启动时加载一次、永不卸载（不调用 DontDestroyOnLoad；卸载只针对关卡场景），
/// 其中的玩家（含准星 Canvas / EventSystem 子物体）因此跨关卡持续存在。玩家不放进关卡场景、
/// 也不放进 Persistance。出生位置固定在 Player 场景的 Player 对象上（直接拖它即可）。
/// 玩家碰撞体注入有 Interactable.ReapplyPlayerCollisionIgnore 兜底，并行加载时序不再敏感。
///
/// 时序（严格遵循）：
///   T0 玩家在出口读卡器上刷卡成功 → 后台异步加载下一关（门保持关闭且锁定 —— 关闭的门本身就是屏障）
///   T1 加载完成 → 激活新场景、解析入口锚点 → 门自动滑开 → 激活出口锚点上的传送门（PortalDoor）
///      （门打开的瞬间，门面上的门后渲染纹理已实时显示下一关，玩家看到完整画面的瞬间它已存在）
///   T2 玩家穿过门洞 → PortalDoor 把玩家传送到下一关入口门洞（速度 / 朝向同步换算）→ 结算过渡
///   T3 结算后立即异步卸载上一关 → 清理资源（门是单向的：前场景已卸载，无法返回）
///
/// 无缝原理：下一关独立摆放在自己的世界坐标（不再对齐拼接），门洞由传送门系统渲染
/// （PortalDoor 用"相对门的位置与玩家相对门的位置相同"的相机生成门面纹理），
/// 穿过瞬间玩家被传送到下一关门洞的对应位置，画面天然连续。
///
/// 防异常：防重复加载（loadState）、防重复卸载/结算（unloading / passFinalized）、
/// 卸载期间禁止新加载（pendingTransition 挂起续传）、最后一关无出口锚点全程空值安全。Play Mode only。
/// </summary>
public class LevelTransitionManager : MonoBehaviour
{
    public static LevelTransitionManager Instance { get; private set; }

    /// <summary>当前关卡索引（CardReader 用它校验卡片归属，防止上一关的卡刷开下一关的门）。</summary>
    public int CurrentLevelIndex => currentLevelIndex;

#if UNITY_EDITOR
    [SerializeField, Tooltip("关卡列表：Inspector 按顺序拖入（不含 Persistance 启动场景）")]
    private SceneAsset[] levels;
#endif

    // 运行时用场景路径加载（SceneAsset 是编辑器专属类型，构建后为 null，故镜像为路径）
    [SerializeField, HideInInspector]
    private string[] levelPaths;

#if UNITY_EDITOR
    [SerializeField, Tooltip("玩家场景（Player.unity）：启动时加载一次、永不卸载，与 Persistance 相同的常驻机制（不调用 DontDestroyOnLoad，关卡卸载只针对关卡场景）")]
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

    private int currentLevelIndex = -1;         // 当前关卡索引
    private LoadState loadState = LoadState.Idle;       // 加载状态
    private bool unloading;                     // 是否正在卸载（防重复卸载；卸载期间禁止新加载）
    private bool passFinalized;                 // 本次过渡是否已结算（防重复结算）
    private bool transitionTriggered;           // 本关是否已刷卡触发过渡（防重复触发）
    private bool pendingTransition;             // 卸载期间拾卡 → 挂起，卸载完成后自动续传

    private Scene currentScene;                 // 当前关卡场景
    private Scene nextScene;                    // 正在加载的下一关场景（T1 激活目标）
    private Scene prevScene;                    // 上一关卡场景（T3 卸载目标）
    private SlidingDoor exitDoor;               // 当前关出口门（下一段过渡使用）
    private Transform exitAnchor;               // 当前关出口锚点（传送门位姿基准）
    private PortalDoor portal;                  // 当前关出口门上的传送门（下一段过渡 T1 激活）
    private Transform entryAnchor;              // 下一关入口锚点（T1 解析，传送门位姿基准）

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
        // 校验 levelPaths：序列化引用若失效（levels 的 SceneAsset 引用未解析成功，
        // OnValidate 会把 levelPaths 写成 null 条目），则从 Build Settings 推导关卡列表
        //（按路径跳过常驻场景：本管理器所在场景 Persistance + 玩家场景 Player，不按序号假设）。
        if (levelPaths == null || levelPaths.Length == 0 || string.IsNullOrEmpty(levelPaths[0]))
        {
            var fallback = new List<string>();
            string ownScenePath = gameObject.scene.path;
            for (int i = 0; i < SceneManager.sceneCountInBuildSettings; i++)
            {
                var p = SceneUtility.GetScenePathByBuildIndex(i);
                if (string.IsNullOrEmpty(p) || p == ownScenePath || p == playerScenePath) continue;
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
        StartCoroutine(BootSequence());
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

    // ==================== 启动引导 ====================

    private IEnumerator BootSequence()
    {
        // 玩家场景与第 0 关并行加载（互不依赖，比串行快约一半；两个协程交错推进）：
        // 玩家碰撞体注入有 Interactable.ReapplyPlayerCollisionIgnore 兜底，
        // 即使玩家晚于关卡激活，拾取物的碰撞忽略也会补上，时序不再敏感。
        var playerOp = StartCoroutine(LoadPlayerSceneCoroutine());
        var levelOp = StartCoroutine(LoadLevelCoroutine(0));
        yield return playerOp;
        yield return levelOp;

        // 加载第 0 关（第一个关卡，无入口锚点，不需要传送门），然后解析并锁定其出口门
        ResolveExitForCurrentLevel();
    }

    /// <summary>
    /// 加载玩家场景（Player.unity）为常驻场景：与 Persistance 一样启动时加载一次、永不卸载，
    /// 其中的玩家（含准星 Canvas / EventSystem 子物体）因此跨关卡持续存在 —— 不调用
    /// DontDestroyOnLoad，关卡卸载（T3）只针对关卡场景 prevScene，玩家场景不可能被卸载。
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

        // 常驻机制 = 永不卸载的场景本身：玩家 / 准星 Canvas / EventSystem 随场景跨关卡存活
        Debug.Log($"[LevelTransitionManager] 玩家场景已加载（常驻，永不卸载）：{playerScenePath}", this);
    }

    private void ResolveExitForCurrentLevel()
    {
        // 解析当前关出口锚点与出口门（最后一关没有出口锚点 → 空值安全）
        exitAnchor = LevelAnchor.FindAnchor(currentScene, LevelAnchor.AnchorType.Exit);
        exitDoor = exitAnchor != null ? exitAnchor.GetComponent<LevelAnchor>().ResolveExitDoor() : null;

        // 关卡识别：出口门必须声明它属于当前关卡（防止误配 / 卡片刷开别的关的门）
        if (exitDoor != null && exitDoor.LevelIndex >= 0 && exitDoor.LevelIndex != currentLevelIndex)
        {
            Debug.LogError($"[LevelTransitionManager] {currentScene.name} 的出口门归属关卡({exitDoor.LevelIndex})与当前关卡({currentLevelIndex})不符，忽略该门", this);
            exitDoor = null;
        }

        // 出口锚点上的传送门（下一段过渡 T1 时激活）
        portal = exitAnchor != null ? exitAnchor.GetComponentInChildren<PortalDoor>(true) : null;

        if (exitDoor != null)
        {
            exitDoor.SetLocked(true);   // 门默认锁定，玩家无法通过
            Debug.Log($"[LevelTransitionManager] {currentScene.name} 出口门已解析并锁定", this);
        }
        else
        {
            Debug.Log($"[LevelTransitionManager] {currentScene.name} 没有出口门（最后一关或未配置）", this);
        }
    }

    // ==================== T0：刷卡 ====================

    /// <summary>
    /// T0：玩家在出口读卡器上刷卡成功（CardReader 调用）。
    /// 只有当前关出口门对应的读卡器有效 —— 走回头路刷旧关的卡 / 刷错门一律拒绝。
    /// 刷卡只负责触发过渡；门的开/关时机全部由管理器控制（加载完成才开门）。
    /// </summary>
    public bool OnCardSwiped(SlidingDoor swipedDoor)
    {
        if (swipedDoor != exitDoor)
        {
            Debug.Log("[LevelTransitionManager] 刷的不是当前出口门，忽略", this);
            return false;
        }

        if (transitionTriggered)
        {
            Debug.Log("[LevelTransitionManager] 已触发过过渡，忽略重复刷卡", this);
            return false;
        }
        transitionTriggered = true;

        // 卸载期间刷卡：挂起，卸载完成后自动续传（防"卸载中加载"状态冲突）
        if (unloading)
        {
            Debug.Log("[LevelTransitionManager] 正在卸载上一关，过渡挂起，卸载完成后继续", this);
            return true;
        }

        BeginTransition();
        return true;
    }

    private void BeginTransition()
    {
        GameEvents.TransitionStart?.Invoke();   // 过渡开始音效(注册式同步;覆盖刷卡触发与卸载挂起续传两个入口)

        // 防重复加载：只有"加载进行中"才拒绝；Done（上一段过渡已结束）允许开启新过渡
        if (loadState == LoadState.Loading)
        {
            Debug.Log("[LevelTransitionManager] 已有过渡进行中，忽略");
            return;
        }

        // 新一轮过渡：重置上次过渡的结算状态
        passFinalized = false;
        nextScene = default;

        if (currentLevelIndex + 1 >= levelPaths.Length)
        {
            // 最后一关：没有下一关可加载，直接解锁开门（可作关卡终点门）
            Debug.Log("[LevelTransitionManager] 已是最后一关，没有下一关可加载");
            loadState = LoadState.Done;
            if (exitDoor != null)
            {
                exitDoor.SetLocked(false);
                exitDoor.OpenDoor();
            }
            return;
        }

        // T0：只启动后台加载。门保持关闭且锁定 —— 加载完成前玩家不可能通过，
        //     门打开时下一关必然已经加载完成（见 LoadNextAndOpen）。
        loadState = LoadState.Loading;
        Debug.Log($"[LevelTransitionManager] T0: 刷卡成功，后台加载下一关 {levelPaths[currentLevelIndex + 1]}（门保持关闭）", this);
        StartCoroutine(LoadNextAndOpen());
    }

    // ==================== T1：加载 + 开门 ====================

    private IEnumerator LoadNextAndOpen()
    {
        int nextIndex = currentLevelIndex + 1;
        if (nextIndex < 0 || nextIndex >= levelPaths.Length) yield break;
        float startTime = Time.time;   // T0 时刻：用于让"门保持关闭"阶段可见
        var op = SceneManager.LoadSceneAsync(levelPaths[nextIndex], LoadSceneMode.Additive);
        if (op == null)
        {
            Debug.LogError($"[LevelTransitionManager] 场景加载失败（op 为 null）：{levelPaths[nextIndex]}", this);
            loadState = LoadState.Idle;
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
        //     门打开时下一关已完整就位：玩家看到门后画面的瞬间它已存在，无任何加载痕迹。
        nextScene = SceneManager.GetSceneByPath(levelPaths[nextIndex]);
        SceneManager.SetActiveScene(nextScene);
        entryAnchor = LevelAnchor.FindAnchor(nextScene, LevelAnchor.AnchorType.Entry);
        if (entryAnchor == null)
            Debug.LogError($"[LevelTransitionManager] 下一关 {nextScene.name} 缺少入口锚点（LevelAnchor Entry），传送门无法工作", this);
        loadState = LoadState.Done;

        // 顺序保证：加载已在上方完成；门再等满 minDoorCloseTime 才打开，
        // 让"拾卡 → 门保持关闭 → 加载 → 开门"的顺序可见（加载瞬间完成时也先关够时间）。
        float remaining = minDoorCloseTime - (Time.time - startTime);
        if (remaining > 0f)
            yield return new WaitForSeconds(remaining);

        if (exitDoor != null)
        {
            exitDoor.SetLocked(false);
            exitDoor.OpenDoor();
        }
        // 传送门与门同步激活：门开瞬间门面即显示下一关实时画面
        if (portal != null && entryAnchor != null)
            portal.Activate(entryAnchor);
        GameEvents.LevelReady?.Invoke(nextScene);   // 门开瞬间同步切换新关环境音(注册式同步)

        Debug.Log($"[LevelTransitionManager] T1: 下一关 {nextScene.name} 加载完成，门开启（刷卡到开门共 {Time.time - startTime:F2}s）", this);
    }

    // ==================== T2：穿过结算 ====================

    /// <summary>T2：玩家穿过传送门（PortalDoor 已把玩家传送到下一关入口门洞）。</summary>
    public void OnPortalCrossed(PortalDoor sender)
    {
        if (passFinalized) return;   // 防重复结算

        passFinalized = true;
        transitionTriggered = false; // 新关卡需要在新关出口读卡器上重新刷卡
        GameEvents.LevelConfirm?.Invoke();   // 关卡切换确认音效(注册式同步)

        // 旧关信息留给卸载使用
        prevScene = currentScene;

        // 切到新关卡
        currentScene = nextScene;
        currentLevelIndex++;
        Debug.Log($"[LevelTransitionManager] T2: 传送穿过，当前关卡 = {currentScene.name}", this);

        // 解析新关出口门并预锁（下一段过渡的门口）
        ResolveExitForCurrentLevel();

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

        // 卸载期间刷了新关的卡 → 现在续传过渡
        if (pendingTransition)
        {
            pendingTransition = false;
            BeginTransition();
        }
    }

    // ==================== 通用：加载一个关卡（启动引导用） ====================

    private IEnumerator LoadLevelCoroutine(int index)
    {
        if (index < 0 || index >= levelPaths.Length) yield break;
        var op = SceneManager.LoadSceneAsync(levelPaths[index], LoadSceneMode.Additive);
        if (op == null)
        {
            Debug.LogError($"[LevelTransitionManager] 场景加载失败（op 为 null）：{levelPaths[index]}", this);
            yield break;
        }
        yield return op;
        currentScene = SceneManager.GetSceneByPath(levelPaths[index]);
        currentLevelIndex = index;
        loadState = LoadState.Done;
        transitionTriggered = false;
        SceneManager.SetActiveScene(currentScene);
        GameEvents.LevelReady?.Invoke(currentScene);   // 启动完成:开当前关卡环境音(注册式同步)
    }
}
