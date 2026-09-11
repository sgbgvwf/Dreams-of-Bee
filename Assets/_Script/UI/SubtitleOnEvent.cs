using UnityEngine;

/// <summary>字幕可以订阅的事件（与 Core/GameEvents.cs 一一对应）。</summary>
public enum SubtitleEventKind
{
    // 玩家飞行
    BeeStateChanged,        // 飞行状态迁移（爬行 / 飞行 / 坠落）
    CrawlStep,              // 爬行每走一步（每步都发，注意）
    // 交互
    PickUp,                 // 拾取成功
    Drop,                   // 放下
    AimGained,              // 准星获得描边（换目标就发，注意）
    AimLost,                // 准星丢失描边（同上）
    SwitchToggled,          // 开关拨动
    // 观察
    PaperOpen,              // 阅读界面打开
    PaperClose,             // 阅读界面关闭
    // 门（抽屉 / 摆动开关也借用其中几个）
    DoorOpen,
    DoorClose,
    DoorSlideStart,
    DoorSlideEnd,
    DoorClunk,
    DoorDeny,               // 上锁时开门被拒
    DoorLock,
    DoorUnlock,
    // 读卡器
    CardSuccess,
    CardDeny,
    // 关卡过渡
    TransitionStart,
    LevelConfirm,
    UnloadFade,
    // 灯
    FlickerCrackle,         // 闪烁灯断电瞬间
    // 流程
    FlowStateChanged,       // 进出主菜单 / 游玩 / 结局
    EndingReached,          // 结局被触发
}

/// <summary>
/// 字幕事件触发 —— 游戏里发生某件事时，播放指定的字幕序列。
///
/// 订阅哪个事件在 Inspector 下拉里选（全部 25 个事实事件都能选）。载荷一律不看：
/// 字幕只关心「这件事发生了」。要区分是谁发的，那是事件本身该带的信息 ——
/// 本组件不做按位置 / 按来源的筛选。
///
/// 与事件管线的约定（见 Core/GameEvents.cs）：本组件是常驻订阅方，
/// OnEnable 用【实例方法】订阅、OnDisable 成对退订 —— 静态委托跨场景持久，
/// 不退订会悬挂。禁止在这里用 lambda 订阅。
///
/// 为什么要判「本场景是不是当前关卡」：目的地关在刷卡那一刻就被后台预加载了（T0），
/// 而「当前关卡」要到穿门结算（T2）才切过去。这中间玩家还在上一关活动 —— 不加这道门，
/// 玩家在旧关开个门，新关已经载好的订阅就先响了一次，once 被白白吃掉，等玩家真的
/// 走进来反而不播了。所以没轮到自己当关卡之前，事件一律不理。
/// （注：LevelConfirm 是在 currentScene 切换之前发的，所以新关里订阅它对不上这道门 ——
/// 这是该事件本身的次序，不是本组件的问题。）
///
/// 计时 / 显隐本身在 SubtitleSequence 里，本组件只管「什么时候叫它播」。
/// </summary>
public class SubtitleOnEvent : MonoBehaviour
{
    [SerializeField, Tooltip("要订阅哪个事实事件（全部 25 个都在下拉里）。")]
    private SubtitleEventKind kind = SubtitleEventKind.DoorOpen;
    [SerializeField, Tooltip("事件发生时播放的字幕序列（要拖同一场景里的 SubtitleSequence）。")]
    private SubtitleSequence sequence;
    [SerializeField, Tooltip("只响应第一次（取消勾选 = 事件每发生一次就播一遍）。")]
    private bool once = true;

    private bool fired;
    private bool warnedNoSequence;

    private void OnEnable()
    {
        // 事件管线约定：静态委托跨场景持久 —— 实例方法订阅，OnDisable 成对退订
        Subscribe();
    }

    private void OnDisable()
    {
        Unsubscribe();
    }

    // ==================== 触发判定 ====================

    private void TryPlay()
    {
        if (once && fired) return;
        if (!IsMySceneCurrentLevel()) return;   // 预加载窗口里不当真，见类注释

        if (sequence == null)
        {
            if (!warnedNoSequence)
            {
                warnedNoSequence = true;
                Debug.LogError($"[SubtitleOnEvent] {name}: 没拖 SubtitleSequence —— {kind} 发生时没东西可播。请在 Inspector 里把要播的字幕序列拖进来", this);
            }
            return;
        }

        sequence.Play();
        fired = true;
    }

    /// <summary>
    /// 本场景是不是「当前关卡」（判定理由见类注释）。没有过渡系统 / 拿不到当前关
    /// （直接 Play 房间调试、主菜单背景）一律放行，让调试时也能正常出字幕。
    /// </summary>
    private bool IsMySceneCurrentLevel()
    {
        var ltm = LevelTransitionManager.Instance;
        if (ltm == null) return true;

        string current = ltm.CurrentLevelPath;
        if (string.IsNullOrEmpty(current)) return true;

        var scene = gameObject.scene;
        if (!scene.IsValid() || string.IsNullOrEmpty(scene.path)) return true;

        return current == scene.path;
    }

    // ==================== 订阅 / 退订（两个 switch 必须逐行对齐） ====================

    private void Subscribe()
    {
        switch (kind)
        {
            case SubtitleEventKind.BeeStateChanged:  GameEvents.BeeStateChanged += OnFiredBeeState; break;
            case SubtitleEventKind.CrawlStep:        GameEvents.CrawlStep += OnFired; break;
            case SubtitleEventKind.PickUp:           GameEvents.PickUp += OnFired; break;
            case SubtitleEventKind.Drop:             GameEvents.Drop += OnFiredAt; break;
            case SubtitleEventKind.AimGained:        GameEvents.AimGained += OnFired; break;
            case SubtitleEventKind.AimLost:          GameEvents.AimLost += OnFired; break;
            case SubtitleEventKind.SwitchToggled:    GameEvents.SwitchToggled += OnFiredAt; break;
            case SubtitleEventKind.PaperOpen:        GameEvents.PaperOpen += OnFired; break;
            case SubtitleEventKind.PaperClose:       GameEvents.PaperClose += OnFired; break;
            case SubtitleEventKind.DoorOpen:         GameEvents.DoorOpen += OnFiredAt; break;
            case SubtitleEventKind.DoorClose:        GameEvents.DoorClose += OnFiredAt; break;
            case SubtitleEventKind.DoorSlideStart:   GameEvents.DoorSlideStart += OnFiredTransform; break;
            case SubtitleEventKind.DoorSlideEnd:     GameEvents.DoorSlideEnd += OnFiredTransform; break;
            case SubtitleEventKind.DoorClunk:        GameEvents.DoorClunk += OnFiredAt; break;
            case SubtitleEventKind.DoorDeny:         GameEvents.DoorDeny += OnFiredAt; break;
            case SubtitleEventKind.DoorLock:         GameEvents.DoorLock += OnFiredAt; break;
            case SubtitleEventKind.DoorUnlock:       GameEvents.DoorUnlock += OnFiredAt; break;
            case SubtitleEventKind.CardSuccess:      GameEvents.CardSuccess += OnFiredAt; break;
            case SubtitleEventKind.CardDeny:         GameEvents.CardDeny += OnFiredAt; break;
            case SubtitleEventKind.TransitionStart:  GameEvents.TransitionStart += OnFired; break;
            case SubtitleEventKind.LevelConfirm:     GameEvents.LevelConfirm += OnFired; break;
            case SubtitleEventKind.UnloadFade:       GameEvents.UnloadFade += OnFired; break;
            case SubtitleEventKind.FlickerCrackle:   GameEvents.FlickerCrackle += OnFiredAt; break;
            case SubtitleEventKind.FlowStateChanged: GameEvents.FlowStateChanged += OnFiredFlow; break;
            case SubtitleEventKind.EndingReached:    GameEvents.EndingReached += OnFiredString; break;
        }
    }

    private void Unsubscribe()
    {
        switch (kind)
        {
            case SubtitleEventKind.BeeStateChanged:  GameEvents.BeeStateChanged -= OnFiredBeeState; break;
            case SubtitleEventKind.CrawlStep:        GameEvents.CrawlStep -= OnFired; break;
            case SubtitleEventKind.PickUp:           GameEvents.PickUp -= OnFired; break;
            case SubtitleEventKind.Drop:             GameEvents.Drop -= OnFiredAt; break;
            case SubtitleEventKind.AimGained:        GameEvents.AimGained -= OnFired; break;
            case SubtitleEventKind.AimLost:          GameEvents.AimLost -= OnFired; break;
            case SubtitleEventKind.SwitchToggled:    GameEvents.SwitchToggled -= OnFiredAt; break;
            case SubtitleEventKind.PaperOpen:        GameEvents.PaperOpen -= OnFired; break;
            case SubtitleEventKind.PaperClose:       GameEvents.PaperClose -= OnFired; break;
            case SubtitleEventKind.DoorOpen:         GameEvents.DoorOpen -= OnFiredAt; break;
            case SubtitleEventKind.DoorClose:        GameEvents.DoorClose -= OnFiredAt; break;
            case SubtitleEventKind.DoorSlideStart:   GameEvents.DoorSlideStart -= OnFiredTransform; break;
            case SubtitleEventKind.DoorSlideEnd:     GameEvents.DoorSlideEnd -= OnFiredTransform; break;
            case SubtitleEventKind.DoorClunk:        GameEvents.DoorClunk -= OnFiredAt; break;
            case SubtitleEventKind.DoorDeny:         GameEvents.DoorDeny -= OnFiredAt; break;
            case SubtitleEventKind.DoorLock:         GameEvents.DoorLock -= OnFiredAt; break;
            case SubtitleEventKind.DoorUnlock:       GameEvents.DoorUnlock -= OnFiredAt; break;
            case SubtitleEventKind.CardSuccess:      GameEvents.CardSuccess -= OnFiredAt; break;
            case SubtitleEventKind.CardDeny:         GameEvents.CardDeny -= OnFiredAt; break;
            case SubtitleEventKind.TransitionStart:  GameEvents.TransitionStart -= OnFired; break;
            case SubtitleEventKind.LevelConfirm:     GameEvents.LevelConfirm -= OnFired; break;
            case SubtitleEventKind.UnloadFade:       GameEvents.UnloadFade -= OnFired; break;
            case SubtitleEventKind.FlickerCrackle:   GameEvents.FlickerCrackle -= OnFiredAt; break;
            case SubtitleEventKind.FlowStateChanged: GameEvents.FlowStateChanged -= OnFiredFlow; break;
            case SubtitleEventKind.EndingReached:    GameEvents.EndingReached -= OnFiredString; break;
        }
    }

    // ==================== 处理器（按签名归类；载荷一律不看，见类注释） ====================

    private void OnFired() { TryPlay(); }
    private void OnFiredAt(Vector3 position) { TryPlay(); }
    private void OnFiredTransform(Transform source) { TryPlay(); }
    private void OnFiredBeeState(BeeFlightController.BeeState prev, BeeFlightController.BeeState next) { TryPlay(); }
    private void OnFiredFlow(FlowState prev, FlowState next) { TryPlay(); }
    private void OnFiredString(string endingId) { TryPlay(); }
}
