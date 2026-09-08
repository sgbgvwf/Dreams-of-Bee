/// <summary>
/// 游戏音效事件标识。每个枚举项对应 Assets/Resources/SFX/ 下的一个素材文件名,
/// 缺失时由 AudioManager 程序生成占位音代替(见 AudioManager.SfxDefs 表)。
/// 枚举顺序必须与 AudioManager.SfxDefs 表的顺序一致。
/// </summary>
public enum SfxId
{
    // === 玩家飞行 ===
    TakeOff,          // 起飞
    Landing,          // 落地
    FallStart,        // 开始下落
    CrawlStep,        // 爬行脚步
    FallWind,         // 下落风声(循环)

    // === 交互 ===
    PickUp,           // 拾取物品
    Drop,             // 放下物品
    AimOn,            // 准星描边获得
    AimOff,           // 准星描边丢失
    SwitchClick,      // 开关拨动(台灯等)

    // === 门 ===
    DoorOpen,         // 开门
    DoorClose,        // 关门
    DoorSlideLoop,    // 滑门马达(循环)
    DoorClunk,        // 门滑动到位碰撞
    DoorDeny,         // 上锁时拒绝开门
    DoorLock,         // 门上锁
    DoorUnlock,       // 门解锁

    // === 读卡器 ===
    CardSuccess,      // 刷卡成功
    CardDeny,         // 刷卡被拒

    // === 关卡过渡 ===
    TransitionWhoosh, // 过渡开始
    LevelConfirm,     // 关卡切换确认
    UnloadFade,       // 关卡卸载淡出

    // === 灯 ===
    FlickerBuzz,      // 灯闪烁嗡鸣(循环)
    FlickerCrackle,   // 灯断电噼啪
}
