using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 存档 / 元数据的数据传输对象(DTO)集合。全部 [Serializable] + 公共字段,JsonUtility 直序列化。
///
/// JsonUtility 约束(已在结构上规避):
///   - 无字典 → 键值对用 KvEntry 列表;
///   - 无多态 → 场景物件状态条目(SaveableEntry)把"组件自己的 DTO"打成 JSON 字符串内嵌,
///     组件类型由 type 标识、身份由场景内 transform 路径标识(见 TransformPath);
///   - 枚举只存整数(BeeState 等以 int 保存);
///   - Vector3 / Quaternion 原生支持。
///
/// schemaVersion:任何字段增删都要 +1 并在此文件补迁移逻辑(见各读取方),旧文件缺失字段由
/// JsonUtility 填默认值 —— 新增可选字段天然向后兼容,删除 / 改名字段才需要显式迁移。
/// </summary>
public static class SaveSchema
{
    /// <summary>当前存档 schema 版本。修改本文件结构后 +1。</summary>
    public const int CurrentVersion = 2;

    /// <summary>手动存档槽位数(固定 3)。</summary>
    public const int SlotCount = 3;

    /// <summary>默认菜单 / 结局背景场景文件名(Build Settings 内按此名查找)。</summary>
    public const string BackdropSceneFileName = "Room_00.unity";
}

/// <summary>通用键值条目:跨局(meta)与局内(run)共用;一档文件里每个 key 至多一条,类型字段四选一。</summary>
[Serializable]
public sealed class KvEntry
{
    public string key = "";
    public bool bVal;
    public int iVal;
    public float fVal;
    public string sVal = "";
}

/// <summary>跨局元数据(meta.json):多周目 / 结局解锁的持久事实,与具体档位无关。</summary>
[Serializable]
public sealed class MetaData
{
    public int schemaVersion = SaveSchema.CurrentVersion;
    /// <summary>累计通关周目数(只在结局确认后 +1,中途退出不算)。新开局的周目号 = completedRuns + 1。</summary>
    public int completedRuns;
    /// <summary>已达成(解锁)的结局 id 列表(去重,保序)。</summary>
    public List<string> seenEndings = new List<string>();
    /// <summary>最后游玩的存档槽索引(跨会话记忆,"继续游戏"读它;-1 = 无)。</summary>
    public int lastSlotIndex = -1;
    /// <summary>作者级全局键值表(周目差异内容 / 结局解锁副作用,运行时只读语义,见 SaveSystem)。</summary>
    public List<KvEntry> kv = new List<KvEntry>();
}

/// <summary>单个存档槽(Slot N.json):一局进度的完整快照(当前关卡 + 该关物件全量状态 + 玩家)。</summary>
[Serializable]
public sealed class SlotData
{
    public int schemaVersion = SaveSchema.CurrentVersion;
    /// <summary>存档时刻(DateTime.UtcNow.Ticks;UI 显示本地时间用)。</summary>
    public long savedAtTicks;
    /// <summary>本档创建时刻(首次开这档的时间;老档无此字段 = 0,读档时回填为保存时刻)。</summary>
    public long createdTicks;
    /// <summary>本档累计游玩秒数(跨会话累计;仅菜单展示)。</summary>
    public float playSeconds;
    /// <summary>本局周目号(新建档时 = meta.completedRuns + 1;仅展示 / 内容差异用)。</summary>
    public int playthroughNumber;
    /// <summary>本局是否已以结局结束(内容已清空;"继续"该档 = 直接开新周目)。</summary>
    public bool runFinished;
    /// <summary>当前关卡场景路径(唯一事实来源;按路径读档,不依赖列表序号)。</summary>
    public string levelPath = "";
    /// <summary>局内键值表(随档存取;作者内容查询口见 SaveSystem.GetRun*)。</summary>
    public List<KvEntry> runKv = new List<KvEntry>();
    /// <summary>当前关卡内全部可存档物件的状态条目(见 ISceneSaveable)。</summary>
    public List<SaveableEntry> sceneState = new List<SaveableEntry>();
    /// <summary>玩家状态快照(存档瞬间从控制器 / 刚体镜像)。</summary>
    public PlayerSnapshot player = new PlayerSnapshot();
}

/// <summary>场景物件状态条目:type = 组件标识(与 ISceneSaveable.SaveableType 配对校验),
/// path = 场景根→物件的名字路径(TransformPath),data = 组件自身 DTO 的 JSON。
/// 恢复时:按 path 找组件 → type 校验 → 调组件 RestoreFromJson;未知 type / 找不到组件 = 警告并跳过。</summary>
[Serializable]
public sealed class SaveableEntry
{
    public string type = "";
    public string path = "";
    public string data = "";
}

// === 组件自有 DTO(内容由对应组件 CaptureToJson / RestoreFromJson 打包,中心 schema 不感知具体类型) ===

/// <summary>SlidingDoor 状态。</summary>
[Serializable]
public sealed class DoorState
{
    public bool locked;
    public bool open;
}

/// <summary>LampSwitchController 状态。</summary>
[Serializable]
public sealed class LampState
{
    public bool on;
}

/// <summary>SwingSwitch 状态(机关单向开启:true = 已开启并永久保持,开过即退役)。</summary>
[Serializable]
public sealed class SwingState
{
    public bool swung;
}

/// <summary>可拾取物(Interactable, Type==Pickup)状态。</summary>
[Serializable]
public sealed class PickupState
{
    public Vector3 position;
    public Quaternion rotation;
    public Vector3 localScale;
}

/// <summary>玩家状态快照(存档瞬间;恢复时直接喂给 BeeFlightController / BeeInteractionController)。</summary>
[Serializable]
public sealed class PlayerSnapshot
{
    public Vector3 position;
    public Quaternion rotation;
    public Vector3 localScale;
    public Vector3 velocity;
    /// <summary>BeeFlightController.BeeState 的整数值。</summary>
    public int beeState;
    public float stamina;
    public float maxStamina;
    public Quaternion lookRotation;
    public bool isHolding;
    /// <summary>所持物根的场景内 transform 路径(采集瞬间的活引用;空 = 未持物)。
    /// itemId 全空时这是持物恢复的唯一身份。</summary>
    public string heldObjectPath = "";
    /// <summary>Interactable.ItemId 原样镜像(日后配置了身份可作第二键)。</summary>
    public string heldItemId = "";
}

/// <summary>
/// 槽位状态分类(菜单 UI 据此决定每个槽的可用操作与文案):
/// 空档(无文件 / 损坏)只能"用新游戏覆盖";进行中可继续 / 覆盖 / 删除;
/// 已通关只剩通关记录(无可玩快照):存档页"继续"会被流程转成开新周目,标题"继续游戏"不可点。
/// </summary>
public enum SlotKind
{
    /// <summary>无内容(文件不存在或损坏已隔离)。</summary>
    Empty,
    /// <summary>进行中(有可玩快照,可继续)。</summary>
    InProgress,
    /// <summary>已通关(只剩通关记录,无快照;新周目可经存档页"继续"自动转,或"用新游戏覆盖")。</summary>
    Finished,
}

/// <summary>槽位摘要(菜单 UI 显示用;只读文件信息,不反序列化完整场景状态)。</summary>
public struct SlotSummary
{
    public bool exists;
    public bool runFinished;
    public int playthroughNumber;
    public string levelPath;
    public long savedAtTicks;
    /// <summary>档创建时刻(0 = 无 / 老档)。</summary>
    public long createdTicks;
    /// <summary>累计游玩秒数。</summary>
    public float playSeconds;

    /// <summary>槽位分类(Empty / InProgress / Finished)。UI 依此启停按钮。</summary>
    public SlotKind Kind =>
        !exists ? SlotKind.Empty :
        runFinished ? SlotKind.Finished : SlotKind.InProgress;
}
