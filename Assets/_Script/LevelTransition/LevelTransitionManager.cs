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
/// 时序（严格遵循）：
///   T0 玩家在出口读卡器上刷卡成功 → 后台异步加载下一关（门保持关闭且锁定 —— 关闭的门本身就是屏障）
///   T1 加载完成 → 将下一关场景根对齐（入口锚点 ≡ 当前关出口锚点）→ 门自动滑开
///      （门打开的瞬间，门后已是完整就位的下一关，玩家直接看到，无任何加载痕迹）
///   T2 玩家踏入通过触发器 → 结算过渡（记录当前关卡、解析并预锁新关出口门）
///   T3 玩家踏入卸载触发器 → 直接异步卸载上一关（不等出口门关闭）→ 清理资源
///
/// 极端情况天然消除：加载未完成前门始终关闭（物理+视觉屏障），玩家不可能看到未加载的空白；
/// 若玩家绕开门口（飞行越过围墙）提前进入通过区，管理器记录等待，加载+对齐完成瞬间自动结算，
/// 全程无任何加载 UI / 进度条 / 黑屏。
///
/// 无缝原理：新关卡加载后整体移动其场景根 Transform，使新关入口锚点与当前关出口锚点
/// 在世界空间完全重合 → 玩家穿过门时世界坐标连续，不做任何传送 / 速度 / Transform 修改。
///
/// 防异常：防重复加载（loadState）、防重复卸载/结算（unloading / passFinalized）、
/// 卸载期间禁止新加载（pendingTransition 挂起续传）、未对齐不结算、
/// 最后一关无出口锚点全程空值安全。Play Mode only。
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

    [SerializeField, Tooltip("仅测试用：模拟加载延迟（秒），用于观察‘门保持关闭直到加载完成’与通过区等待逻辑")]
    private float simulatedLoadDelay;

    [SerializeField, Tooltip("拾卡后门最少保持关闭的时长（秒）：即使下一关瞬间加载完，门也先关够这段时间再开，让‘门是屏障、加载完成后才放行’的顺序可见。设为 0 则加载完立即开门")]
    private float minDoorCloseTime = 1f;

    // --- 状态 ---
    private enum LoadState { Idle, Loading, Done }              // 加载状态
    private enum PlayerRegion { None, PassZone, UnloadZone }    // 玩家区域状态

    private int currentLevelIndex = -1;         // 当前关卡索引
    private LoadState loadState = LoadState.Idle;       // 加载状态
    private PlayerRegion playerRegion = PlayerRegion.None;  // 玩家所在区域
    private bool unloading;                     // 是否正在卸载（防重复卸载；卸载期间禁止新加载）
    private bool passFinalized;                 // 本次过渡是否已结算（防重复结算）
    private bool transitionTriggered;           // 本关是否已刷卡触发过渡（防重复触发）
    private bool pendingTransition;             // 卸载期间拾卡 → 挂起，卸载完成后自动续传

    private Scene currentScene;                 // 当前关卡场景
    private Scene nextScene;                    // 正在加载的下一关场景（T1 对齐目标）
    private Scene prevScene;                    // 上一关卡场景（T3 卸载目标）
    private SlidingDoor exitDoor;               // 当前关出口门（下一段过渡使用）
    private Transform exitAnchor;               // 当前关出口锚点

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
        //（第 0 个是常驻的 Persistance 启动场景，跳过）。
        if (levelPaths == null || levelPaths.Length == 0 || string.IsNullOrEmpty(levelPaths[0]))
        {
            var fallback = new List<string>();
            for (int i = 1; i < SceneManager.sceneCountInBuildSettings; i++)
            {
                var p = SceneUtility.GetScenePathByBuildIndex(i);
                if (!string.IsNullOrEmpty(p)) fallback.Add(p);
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
        // 加载第 0 关（第一个关卡，无入口锚点，不需要对齐），然后解析并锁定其出口门
        yield return LoadLevelCoroutine(0);
        ResolveExitForCurrentLevel();
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
    /// 刷卡只负责触发过渡；门的开/关时机全部由管理器控制（加载+对齐完成才开门）。
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
        // 防重复加载：只有"加载进行中"才拒绝；Done（上一段过渡已结束）允许开启新过渡
        if (loadState == LoadState.Loading)
        {
            Debug.Log("[LevelTransitionManager] 已有过渡进行中，忽略");
            return;
        }

        // 新一轮过渡：重置上次过渡的结算状态
        passFinalized = false;
        playerRegion = PlayerRegion.None;
        nextScene = default;   // 清掉上一段的通过触发器目标，防止走回头路时误结算

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
        //     门打开时下一关必然已经加载并对齐完成（见 LoadAndAlignNext）。
        loadState = LoadState.Loading;
        Debug.Log($"[LevelTransitionManager] T0: 刷卡成功，后台加载下一关 {levelPaths[currentLevelIndex + 1]}（门保持关闭）", this);
        StartCoroutine(LoadAndAlignNext());
    }

    // ==================== T1：加载 + 对齐 ====================

    private IEnumerator LoadAndAlignNext()
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

        // 先加载内容、不激活：保证场景激活时已完成对齐，玩家看不到"未对齐画面"
        op.allowSceneActivation = false;
        while (op.progress < 0.9f)
            yield return null;

        if (simulatedLoadDelay > 0f)
            yield return new WaitForSeconds(simulatedLoadDelay);   // 仅测试：模拟慢加载

        op.allowSceneActivation = true;
        yield return op;

        // T1：加载完成 → 对齐（激活后同一帧完成，无跳变）→ 解锁开门。
        //     门打开时下一关已完整就位：玩家看到门后场景的瞬间它已存在，无任何加载痕迹。
        nextScene = SceneManager.GetSceneByPath(levelPaths[nextIndex]);
        AlignLevel(nextScene);
        loadState = LoadState.Done;

        // 顺序保证：加载 + 对齐已在上方完成；门再等满 minDoorCloseTime 才打开，
        // 让"拾卡 → 门保持关闭 → 加载 → 开门"的顺序可见（加载瞬间完成时也先关够时间）。
        float remaining = minDoorCloseTime - (Time.time - startTime);
        if (remaining > 0f)
            yield return new WaitForSeconds(remaining);

        if (exitDoor != null)
        {
            exitDoor.SetLocked(false);
            exitDoor.OpenDoor();
        }
        Debug.Log($"[LevelTransitionManager] T1: 下一关 {nextScene.name} 加载并已对齐，门开启", this);

        // 玩家若已等在通过触发器内：立即结算（极端情况的等待点）
        if (playerRegion == PlayerRegion.PassZone)
            FinalizePass(nextScene);
    }

    /// <summary>
    /// 对齐：整体移动下一关场景根，使入口锚点与世界空间当前关出口锚点完全重合。
    /// levelRoot 旋转先对齐朝向，再平移使锚点重合（锚点 localScale 约定恒为 1）。
    /// </summary>
    private void AlignLevel(Scene scene)
    {
        var levelRoot = LevelAnchor.FindLevelRoot(scene);
        var entry = LevelAnchor.FindAnchor(scene, LevelAnchor.AnchorType.Entry);
        if (levelRoot == null || entry == null || exitAnchor == null)
        {
            Debug.LogError("[LevelTransitionManager] 对齐失败：缺少关卡对齐根 / 入口锚点 / 出口锚点", this);
            return;
        }

        levelRoot.rotation = exitAnchor.rotation * Quaternion.Inverse(entry.localRotation);
        levelRoot.position = exitAnchor.position - levelRoot.rotation * Vector3.Scale(entry.localPosition, levelRoot.localScale);

        // 禁用下一关入口门模型（若配置了）：同一连接处只保留一个门模型，避免重叠闪烁
        var entryModel = entry.GetComponent<LevelAnchor>().EntryDoorModel;
        if (entryModel != null) entryModel.SetActive(false);

        Debug.Log($"[LevelTransitionManager] 对齐完成：入口锚点与出口锚点差 {Vector3.Distance(entry.position, exitAnchor.position):F4}", this);

        SceneManager.SetActiveScene(scene);
    }

    // ==================== T2：通过触发器 ====================

    /// <summary>T2：玩家踏入通过触发器（LevelPassTrigger 调用）。</summary>
    public void OnPlayerEnteredPassZone(LevelPassTrigger trigger)
    {
        if (loadState == LoadState.Idle) return;   // 防御：过渡尚未开始

        // 只接受"当前过渡目标关卡"的通过触发器（防止玩家走回头路时误结算）
        if (trigger.gameObject.scene != nextScene)
        {
            Debug.Log("[LevelTransitionManager] 非目标关卡的通过触发器，忽略");
            return;
        }

        if (loadState == LoadState.Done)
        {
            FinalizePass(trigger.gameObject.scene);
        }
        else
        {
            // 加载未完成：记录等待，加载完成回调里自动结算（无 UI 提示）
            playerRegion = PlayerRegion.PassZone;
            Debug.Log("[LevelTransitionManager] T2: 玩家已到通过区，等待加载与对齐完成…");
        }
    }

    /// <summary>结算过渡：记录旧关信息供 T3 卸载，切换当前关卡并预锁新关出口门。</summary>
    private void FinalizePass(Scene passedScene)
    {
        if (passFinalized) return;   // 防重复结算（玩家反复进出触发器）
        if (currentLevelIndex + 1 >= levelPaths.Length) return;   // 兜底：最后一关没有过渡可结算

        passFinalized = true;
        transitionTriggered = false; // 新关卡需要在新关出口读卡器上重新刷卡
        playerRegion = PlayerRegion.None;

        // 旧关信息留给 T3 卸载使用
        prevScene = currentScene;

        // 切到新关卡
        currentScene = passedScene;
        currentLevelIndex++;
        Debug.Log($"[LevelTransitionManager] T2: 过渡结算，当前关卡 = {currentScene.name}", this);

        // 解析新关出口门并预锁（下一段过渡的门口）
        ResolveExitForCurrentLevel();
    }

    // ==================== T3：卸载触发器 ====================

    /// <summary>T3：玩家踏入卸载触发器（LevelUnloadTrigger 调用）。</summary>
    public void OnPlayerEnteredUnloadZone(LevelUnloadTrigger trigger)
    {
        // 防重复卸载 / 未结算不卸载 / 触发器必须属于当前关卡（防走回头路误触发）
        if (unloading || !passFinalized || !prevScene.IsValid()) return;
        if (trigger.gameObject.scene != currentScene) return;

        StartCoroutine(UnloadPrevLevel());
    }

    private IEnumerator UnloadPrevLevel()
    {
        unloading = true;
        Debug.Log("[LevelTransitionManager] T3: 直接卸载上一关", this);

        // 卸载前一关（异步，后台执行，不影响玩家操作；不等待旧关出口门关闭）
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
    }
}
