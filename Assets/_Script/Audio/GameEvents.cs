using System;
using UnityEngine;

/// <summary>
/// 全局音效事件总线(注册式同步):
/// 玩法方法在动作发生的瞬间 raise 对应事件,AudioManager 在 Awake 注册全部处理器,
/// 同一调用栈内同步响应播放。玩法脚本不接触任何音频 API,只管广播;
/// 玩法侧照常每次触发都广播,去重在 AudioManager 内做:同款一次性音效带极小播放冷却
/// (OneShotCooldown 0.05s),同帧 / 瞬间连发的重复只响一次;循环音不受冷却(多源合法并存)。
/// 声明为普通静态委托字段(非 C# event),这样玩法侧可以直接 ?.Invoke() 广播;
/// 约定:注册只能走 +=,不对外赋值/清空。
/// </summary>
public static class GameEvents
{
    // === 玩家飞行状态(载荷:切换前、切换后的状态) ===
    public static Action<BeeFlightController.BeeState, BeeFlightController.BeeState> BeeStateChanged;
    /// <summary>爬行脚步(每走一步触发一次,可重复)。</summary>
    public static Action CrawlStep;

    // === 交互 ===
    /// <summary>拾取物品(玩家身上,2D)。</summary>
    public static Action PickUp;
    /// <summary>放下物品(载荷:掉落点世界位置,3D)。</summary>
    public static Action<Vector3> Drop;
    /// <summary>准星描边获得 / 丢失(可重复)。</summary>
    public static Action AimGained;
    public static Action AimLost;
    /// <summary>开关拨动(台灯等,载荷:开关位置,3D)。</summary>
    public static Action<Vector3> SwitchToggled;

    // === 门 ===
    /// <summary>门开始滑动(载荷:门的当前位置,3D)。</summary>
    public static Action<Vector3> DoorOpen;
    public static Action<Vector3> DoorClose;
    /// <summary>滑门马达循环启 / 停(载荷:门根 Transform,循环跟随门体)。</summary>
    public static Action<Transform> DoorSlideStart;
    public static Action<Transform> DoorSlideEnd;
    /// <summary>门滑动到位(载荷:门位置)。</summary>
    public static Action<Vector3> DoorClunk;
    /// <summary>上锁时拒绝开门(载荷:门位置)。</summary>
    public static Action<Vector3> DoorDeny;
    /// <summary>门上锁 / 解锁(载荷:门位置)。</summary>
    public static Action<Vector3> DoorLock;
    public static Action<Vector3> DoorUnlock;

    // === 读卡器 ===
    public static Action<Vector3> CardSuccess;
    public static Action<Vector3> CardDeny;

    // === 关卡过渡 ===
    /// <summary>过渡开始(刷卡触发 / 卸载挂起续传)。</summary>
    public static Action TransitionStart;
    /// <summary>关卡切换结算确认。</summary>
    public static Action LevelConfirm;
    /// <summary>上一关卸载淡出。</summary>
    public static Action UnloadFade;

    // === 灯 ===
    /// <summary>闪烁灯断电瞬间(载荷:灯位置)。</summary>
    public static Action<Vector3> FlickerCrackle;

    // === 流程(菜单 / 结局;音频侧可后续注册,目前无处理器) ===
    /// <summary>游戏流程状态切换(主菜单 ↔ 游玩 ↔ 结局 ↔ 开发者直玩)。</summary>
    public static Action FlowStateChanged;
    /// <summary>结局被触发(载荷:结局 id)。</summary>
    public static Action<string> EndingReached;
}
