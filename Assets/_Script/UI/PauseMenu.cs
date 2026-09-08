using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// 暂停系统 + 暂停菜单(UI 场景化后只留行为;视觉对象在 Persistance 场景的 "Pause Canvas" 下,
/// 由场景 YAML 摆放并在此绑定按钮)。挂在 Persistance 场景 "Pause Canvas" 的 GameObject 上,
/// 跨关卡常驻 —— 游玩中任何时刻按 Esc 都能开。
///
/// 暂停机制 —— 为什么 timeScale = 0 就足以冻结整个世界:
///   - Time.timeScale = 0 会把 Time.deltaTime 归零、停掉 FixedUpdate 与物理步进;
///     本项目已核实没有任何 unscaledDeltaTime / WaitForSecondsRealtime(除提示回落)用法,
///     全部逻辑(飞行体力结算、灯光闪烁、门、关卡过渡…)都依赖 scaled time,
///     因此 timeScale = 0 时画面与机制自然全部冻结,无需逐个脚本通知。
///   - 而 MonoBehaviour.Update / EventSystem / uGUI 输入照常运行 → 菜单可以点击、可以再按 Esc 恢复。
///   - 播放中的音效(风声循环等)不随 timeScale 停,统一用 AudioListener.pause 静音。
///   - 打开菜单时让出光标控制:BeeFlightController.HandleCursor 每帧会把光标锁回,
///     它读到 PauseMenu.IsPaused 后会让出(见该脚本 Update 首行);光标与 timeScale 由本类统一管理。
///
/// Esc 直接轮询 Keyboard.current(Esc 全项目未被占用),暂不进 Input Action 资产;
/// 日后做手柄 / 按键重映射时再迁移到 Settings/Input Action.inputactions 里。
/// 暂停只属于游玩:主菜单 / 结局时 Esc 不操作暂停(CanOperatePause 门控)。
/// </summary>
public class PauseMenu : MonoBehaviour
{
    /// <summary>常驻实例(Persistance 场景;开发者直玩无此场景时为 null)。</summary>
    public static PauseMenu Instance { get; private set; }

    /// <summary>游戏是否处于暂停(菜单打开)。BeeFlightController 借此让出光标控制。</summary>
    public static bool IsPaused;

    // === 场景引用(在 Persistance 场景的 "Pause Canvas" 下绑定) ===
    [SerializeField, Tooltip("整个菜单视觉根(暂停画布下含遮罩与按钮的根)。开/关 = SetActive。")]
    private GameObject menuRoot;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        // 本场景的 EventSystem 输入模块动作槽为空(场景化后没有运行时自建),补默认 Point/Click 绑定;
        // 找不到(直玩)则跳过 —— 无菜单可点。
        var es = FindObjectOfType<EventSystem>();
        if (es != null)
        {
            var module = es.GetComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
            if (module != null && module.actionsAsset == null)
                module.AssignDefaultActions();
        }
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // Esc 输入不在这里:常驻的 PauseInput 组件在"暂停可被启用"时监听 Esc 并调 Pause()/Resume()。
    // 本组件只提供暂停行为(状态 / 时间 / 光标 / 音频 / 菜单开关)与按钮入口。

    // ==================== 按钮入口(场景按钮的持久 onClick 绑定这些公开方法) ====================

    /// <summary>继续游戏(按钮 / Esc)。</summary>
    public void Resume()
    {
        if (!IsPaused) return;

        SetMenuVisible(false);
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        AudioListener.pause = false;
        Time.timeScale = 1f;          // 最后再恢复时间,避免中间有任何一帧跑出 timeScale=0 的增量逻辑
        IsPaused = false;
    }

    /// <summary>保存游戏到当前活动档(过渡中 / 未就绪会失败;结果走日志)。</summary>
    public void SaveGame()
    {
        bool ok = SaveSystem.SaveActiveSlot();
        Debug.Log(ok ? "[PauseMenu] 已保存到当前档位" : "[PauseMenu] 当前无法保存(过渡中或未就绪)");
    }

    /// <summary>返回主菜单(含自动存档与收局,由流程执行)。</summary>
    public void ToMenuClicked()
    {
        GameFlowManager.QuitToMenu();
    }

    /// <summary>退出游戏(显式出口:退出前先把当前进度落盘 —— 稳定点内必成功;过渡中失败也无害,
    /// 游玩中的周期快照落盘已把硬退出损失限在一个 SnapshotPersistInterval 内)。</summary>
    public void QuitGame()
    {
        SaveSystem.SaveActiveSlot();   // 玩家明确点退出 = 想带走当前进度;非稳定点由 SaveActiveSlot 内部拒绝并留日志
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;   // 编辑器里 Application.Quit 无效,直接退出播放
#else
        Application.Quit();
#endif
    }

    /// <summary>流程强制解除暂停(返回主菜单 / 结局前调用):关菜单、恢复时间 / 音频,
    /// 复位暂停静态状态。光标归属由 GameFlowManager 统一管理(菜单 = 释放)。</summary>
    public static void ForceExitPause()
    {
        if (Instance == null) return;
        Instance.ForceResetPauseUi();
    }

    // ==================== 内部 ====================

    /// <summary>进入暂停(由 PauseInput 的 Esc 监听调用)。</summary>
    public void Pause()
    {
        if (IsPaused) return;

        IsPaused = true;
        Time.timeScale = 0f;          // 冻结一切 scaled-time 逻辑 + 物理(机制见类注释)
        AudioListener.pause = true;   // 风声等正在播放的循环音一并停
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
        SetMenuVisible(true);
    }

    private void ForceResetPauseUi()
    {
        SetMenuVisible(false);
        IsPaused = false;
        Time.timeScale = 1f;          // 时间恢复要在菜单关掉之后,避免半帧跑出 timeScale=0 的增量逻辑
        AudioListener.pause = false;
    }

    private void SetMenuVisible(bool visible)
    {
        if (menuRoot != null) menuRoot.SetActive(visible);
    }
}
