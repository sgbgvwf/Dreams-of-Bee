using System;
using UnityEngine;

/// <summary>
/// 通用事件管线 —— 全项目唯一的瞬间事实广播通道(静态强类型委托):
/// 玩法方法在动作发生的瞬间 raise 对应事件,事件向全体订阅者广播 ——
/// 音效(AudioManager)、UI、流程、内容等任何系统都可订阅,不存在"专供某系统"的事件。
/// 玩法侧只 raise,不接触任何订阅方的 API;订阅方各自决定响应与去重
/// (例:同款一次性音效 0.05s 播放冷却在 AudioManager 内做,玩法照常每次广播)。
///
/// 订阅与生命周期约定(重要):
///   - 静态委托跨场景持久,不会随场景卸载自动清空;
///   - 组件订阅必须用【实例方法】,并在 OnDisable 里成对 -= (示例:ReadingOverlay 订阅 FlowStateChanged);
///     禁止用 lambda 做常驻订阅 —— lambda 无法退订,会悬挂 / 泄漏;
///   - 事件先定义、后接线是合法状态:零订阅者不报错,raise 侧 ?.Invoke 静默跳过;
///   - 声明为普通静态委托字段(非 C# event),raise 侧直接 ?.Invoke();注册只走 +=,不对外赋值 / 清空。
/// </summary>
public static class GameEvents
{
    // === 玩家飞行状态(载荷:切换前、切换后的状态) ===
    /// <summary>飞行状态迁移(爬行 / 飞行 / 坠落;AudioManager 据此推导起飞、落地、坠地音与落风循环)。</summary>
    public static Action<BeeFlightController.BeeState, BeeFlightController.BeeState> BeeStateChanged;
    /// <summary>爬行每走一步(可重复)。</summary>
    public static Action CrawlStep;

    // === 交互 ===
    /// <summary>拾取物品成功(物品到玩家身上)。</summary>
    public static Action PickUp;
    /// <summary>放下物品(载荷:掉落点世界位置)。</summary>
    public static Action<Vector3> Drop;
    /// <summary>准星描边获得 / 丢失(切换目标,可重复)。</summary>
    public static Action AimGained;
    public static Action AimLost;
    /// <summary>开关拨动(台灯 / 机关等,载荷:开关位置)。</summary>
    public static Action<Vector3> SwitchToggled;

    // === 观察(纸张 / 书本 / 海报阅读) ===
    /// <summary>阅读会话打开 / 关闭(ReadingOverlay 会话边界)。</summary>
    public static Action PaperOpen;
    public static Action PaperClose;

    // === 门(注:滑动抽屉 / 摆动开关借用 DoorOpen / DoorClunk / DoorUnlock,事件名带 Door 但来源不限于门) ===
    /// <summary>门开始滑动(载荷:门当前世界位置)。</summary>
    public static Action<Vector3> DoorOpen;
    public static Action<Vector3> DoorClose;
    /// <summary>滑门滑动开始 / 结束(载荷:门根 Transform —— 跟随 / 按门分组用,如马达循环音)。</summary>
    public static Action<Transform> DoorSlideStart;
    public static Action<Transform> DoorSlideEnd;
    /// <summary>滑动到位(抽屉 / 摆动开关借用)。</summary>
    public static Action<Vector3> DoorClunk;
    /// <summary>上锁时收到开门请求被拒(载荷:门位置)。</summary>
    public static Action<Vector3> DoorDeny;
    /// <summary>门上锁 / 解锁(摆动开关对锁刚体解锁时借用,载荷:位置)。</summary>
    public static Action<Vector3> DoorLock;
    public static Action<Vector3> DoorUnlock;

    // === 读卡器 ===
    /// <summary>刷卡成功 / 被拒(载荷:读卡器位置)。</summary>
    public static Action<Vector3> CardSuccess;
    public static Action<Vector3> CardDeny;

    // === 关卡过渡 ===
    /// <summary>过渡开始(刷卡触发 / 卸载挂起续传)。</summary>
    public static Action TransitionStart;
    /// <summary>关卡切换结算确认。</summary>
    public static Action LevelConfirm;
    /// <summary>上一关卸载前(淡出点)。</summary>
    public static Action UnloadFade;

    // === 灯 ===
    /// <summary>闪烁灯断电瞬间(载荷:灯位置)。</summary>
    public static Action<Vector3> FlickerCrackle;

    // === 流程(菜单 / 结局) ===
    /// <summary>流程状态迁移(载荷:切换前、切换后的状态)。同步触发于 GameFlowManager.SetState 内部,
    /// 此时 FlowStateSO 镜像已写入新状态 —— 处理器读镜像与比对载荷等价;暂停不改 FlowState,不触发。</summary>
    public static Action<FlowState, FlowState> FlowStateChanged;
    /// <summary>结局被触发(载荷:结局 id)。</summary>
    public static Action<string> EndingReached;
}
