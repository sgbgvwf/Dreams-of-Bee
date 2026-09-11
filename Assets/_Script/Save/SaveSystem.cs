using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 存档系统（静态，无组件）：文件层 + 采集 / 恢复编排。GameFlowManager 驱动；内容侧只读查询口见下方 API。
///
/// 文件布局（Application.persistentDataPath/Saves/）：
///   meta.json    —— 跨局元数据（多周目 / 结局解锁 / 全局键值表 / 最后游玩槽位）
///   slot0..2.json —— 3 个存档位（SlotData：关卡路径 + 该关全量物件状态 + 玩家快照 +
///                    创建时刻 / 累计游玩时长 + 局内键值表）
///
/// 槽位语义（覆盖制，单一活动档）：开始新游戏 = 选槽（占用则确认覆盖）→ 该槽成为"活动槽"，
/// 此后关键节点自动存档（开局到达 / 每关到达站稳 / 返回主菜单）与手动保存都持续覆盖它。
/// 读档只发生在主菜单：存档页的"继续" = 恢复所选槽（已通关档内容已清空 → 直接开新周目）；
/// 标题"继续游戏"只续「上一次游戏」（meta.lastSlotIndex，仅未通关档可点，见 GameFlowManager）。
///
/// 采集 / 恢复（核心对物件类型封闭，扩展见 ISceneSaveable）：
///   - 采集：扫描当前关卡场景根下所有 ISceneSaveable，条目 = { 类型, 场景内 transform 路径, 组件 DTO JSON }，
///     再镜像玩家（控制器 / 刚体直读，持物记活引用路径）；只在稳定点（LevelTransitionManager.IsSettled）允许；
///   - 恢复：按路径找组件 → 类型校验 → 组件自恢复（场景刚实例化、任何游玩帧之前），随后玩家快照 + 持物重挂；
///     条目组件缺失 / 类型不符 / 路径落空 → 警告并跳过（旧档对新场景结构天然容错）；
///   - 周期落盘（游玩中即写、不依赖存档点）：每 PeriodicPersistInterval 秒一拍 —— 稳定点时写整局
///     快照（时长含在快照里），稳定点外退回纯时长轻量写 —— 直接退出（编辑器 Stop / 关进程 / 崩溃）
///     时长与进度同粒度、最多滞后一个间隔；显式点（到达站稳 / 回菜单 / 手动保存）全量存档不变；
///   - 原子写：先写 .tmp 再 File.Replace（不支持时退化删除+移动），崩溃不会留下半写档；
///     读损坏 → 原档改名 .corrupt 视为空档，不再覆盖好档。
///
/// JsonUtility 约束已由 SaveData.cs 的 DTO 结构规避（无字典 → KvEntry 列表）。Play Mode only。
/// </summary>
public static class SaveSystem
{
    // ==================== 元数据（meta.json） ====================

    private static MetaData meta;
    private static bool metaLoaded;

    /// <summary>跨局元数据（惰性加载；首次访问会建目录 / 默认档）。</summary>
    public static MetaData Meta
    {
        get
        {
            if (!metaLoaded) LoadMeta();
            return meta;
        }
    }

    private static void LoadMeta()
    {
        metaLoaded = true;
        meta = null;
        try
        {
            string path = MetaPath();
            if (File.Exists(path))
                meta = JsonUtility.FromJson<MetaData>(File.ReadAllText(path));
        }
        catch (Exception e)
        {
            // 与读档损坏一致的处理:损坏文件隔离为 .meta.corrupt(原档保留供排查),
            // 否则下次任一次 SaveMeta 都会直接覆写损坏文件 —— completedRuns / 结局 / 全局 KV 全丢
            Debug.LogWarning($"[SaveSystem] meta 损坏,已隔离为 .corrupt 原档保留:{e.Message}");
            try { File.Delete(MetaPath() + ".corrupt"); } catch (Exception) { }
            try { File.Move(MetaPath(), MetaPath() + ".corrupt"); } catch (Exception) { }
        }
        if (meta == null)
        {
            meta = new MetaData();
            Debug.Log("[SaveSystem] 无 meta 存档，已创建默认元数据（首次启动）");
        }
    }

    /// <summary>写回 meta.json（原子；全局 KV / 结局 / 周目变化后调用）。</summary>
    public static void SaveMeta()
    {
        if (meta == null) return;
        WriteFileAtomic(MetaPath(), JsonUtility.ToJson(meta));
    }

    // === 结局与周目（多周目元数据；只有流程在结局确认后调用） ===
    /// <summary>结局是否已达成过（跨局，内容按结局解锁分支时查询）。</summary>
    public static bool IsEndingUnlocked(string endingId)
    {
        if (string.IsNullOrEmpty(endingId)) return false;
        return Meta.seenEndings.Contains(endingId);
    }

    /// <summary>登记一个已达成的结局（去重；有变化才写盘）。返回是否新登记。</summary>
    public static bool RecordEndingSeen(string endingId)
    {
        if (string.IsNullOrEmpty(endingId)) return false;
        if (Meta.seenEndings.Contains(endingId)) return false;
        Meta.seenEndings.Add(endingId);
        SaveMeta();
        return true;
    }

    /// <summary>累计通关周目数只增不减（本局周目号超过旧记录才算新周目）。</summary>
    public static void RecordCompletedRun(int playthroughNumber)
    {
        if (playthroughNumber <= Meta.completedRuns) return;
        Meta.completedRuns = playthroughNumber;
        SaveMeta();
    }

    // === 全局键值表（跨局；周目差异内容的只读查询口 + 作者级受控写入） ===
    public static bool GetGlobalBool(string key, bool def = false) => FindKv(Meta.kv, key, out var e) ? e.bVal : def;
    public static int GetGlobalInt(string key, int def = 0) => FindKv(Meta.kv, key, out var e) ? e.iVal : def;
    public static float GetGlobalFloat(string key, float def = 0f) => FindKv(Meta.kv, key, out var e) ? e.fVal : def;
    public static string GetGlobalString(string key, string def = "") => FindKv(Meta.kv, key, out var e) ? e.sVal : def;

    public static void SetGlobalBool(string key, bool v) { var e = GetOrCreateKv(Meta.kv, key); e.bVal = v; SaveMeta(); }
    public static void SetGlobalInt(string key, int v) { var e = GetOrCreateKv(Meta.kv, key); e.iVal = v; SaveMeta(); }
    public static void SetGlobalFloat(string key, float v) { var e = GetOrCreateKv(Meta.kv, key); e.fVal = v; SaveMeta(); }
    public static void SetGlobalString(string key, string v) { var e = GetOrCreateKv(Meta.kv, key); e.sVal = v; SaveMeta(); }

    // ==================== 局内键值表（随活动档存取；下场即清） ====================

    private static int activeSlotIndex = -1;         // 活动档（新游戏 / 读档开始时选定）
    private static int activePlaythrough = 1;        // 活动局周目号
    private static readonly List<KvEntry> runKvCache = new List<KvEntry>();   // 内存缓存,采集时并入 SlotData
    private static long sessionCreatedTicks;         // 本会话"档创建时刻"(新局 = 现在;读档 = 档内原有值)
    private static float sessionPlaySeconds;         // 本会话开始前该档已累计的游玩秒数(读档带出;新局 = 0)
    private static float sessionClockSeconds;        // 本会话内累计的游玩秒数(游玩中每帧 Tick;暂停不计)
    private static float lastPeriodicPersistSeconds;   // 上次周期落盘时的 sessionClockSeconds(节流基准;见 TickSessionClock)

    /// <summary>周期落盘的节流间隔(秒):游玩中每累计满这么多就落盘一拍(见 PeriodicPersist)。
    /// 时长与进度共用同一条节奏 —— 稳定点时整局快照(时长含在快照里),稳定点外退回纯时长轻量写,
    /// 直接退出(编辑器 Stop / 关进程 / 崩溃)两者都最多滞后一个间隔,不依赖任何存档点。</summary>
    private const float PeriodicPersistInterval = 1f;

    public static bool HasActiveRun => activeSlotIndex >= 0;
    public static int ActiveSlotIndex => activeSlotIndex;
    public static int ActivePlaythrough => activePlaythrough;

    // === 供 UI 同步的状态查询(按钮可用性 / 提示文案直接读这些) ===

    /// <summary>此刻是否可安全存档:有活动档 + 关卡处于稳定点 + 玩家场景在。UI 据此启停"保存"按钮。</summary>
    public static bool CanSaveNow
    {
        get
        {
            if (!HasActiveRun) return false;
            var ltm = LevelTransitionManager.Instance;
            return ltm != null && ltm.IsSettled && ltm.IsPlayerSceneLoaded;
        }
    }

    /// <summary>是否正处于关卡过渡 / 加载中(非稳定点;直玩无 LTM 时恒 true)。UI 提示"过渡中"用。</summary>
    public static bool IsTransitionBusy
    {
        get
        {
            var ltm = LevelTransitionManager.Instance;
            return ltm == null || !ltm.IsSettled;
        }
    }

    /// <summary>最近一次 SaveActiveSlot() 是否成功(false = 无活动档 / 非稳定点 / 玩家未就绪;启动默认 false)。</summary>
    public static bool LastSaveSucceeded { get; private set; }
    /// <summary>本局从第几周目开始（新建档 = completedRuns + 1；读档 = 档内记录）。</summary>
    public static int CurrentPlaythroughNumber => HasActiveRun ? activePlaythrough : Meta.completedRuns + 1;

    public static bool GetRunBool(string key, bool def = false) => HasActiveRun && FindKv(runKvCache, key, out var e) ? e.bVal : def;
    public static int GetRunInt(string key, int def = 0) => HasActiveRun && FindKv(runKvCache, key, out var e) ? e.iVal : def;
    public static float GetRunFloat(string key, float def = 0f) => HasActiveRun && FindKv(runKvCache, key, out var e) ? e.fVal : def;
    public static string GetRunString(string key, string def = "") => HasActiveRun && FindKv(runKvCache, key, out var e) ? e.sVal : def;

    public static void SetRunBool(string key, bool v) { if (HasActiveRun) { GetOrCreateKv(runKvCache, key).bVal = v; } }
    public static void SetRunInt(string key, int v) { if (HasActiveRun) { GetOrCreateKv(runKvCache, key).iVal = v; } }
    public static void SetRunFloat(string key, float v) { if (HasActiveRun) { GetOrCreateKv(runKvCache, key).fVal = v; } }
    public static void SetRunString(string key, string v) { if (HasActiveRun) { GetOrCreateKv(runKvCache, key).sVal = v; } }

    // ==================== 槽位生命周期（GameFlowManager 驱动） ====================

    /// <summary>开始新局：选定活动档（调用方已确认覆盖），清局内键值，记"最后游玩槽"。返回 false 表示槽位索引非法。</summary>
    public static bool BeginNewRunSession(int slotIndex, int playthroughNumber)
    {
        if (slotIndex < 0 || slotIndex >= SaveSchema.SlotCount) return false;
        activeSlotIndex = slotIndex;
        activePlaythrough = playthroughNumber;
        runKvCache.Clear();
        sessionCreatedTicks = DateTime.UtcNow.Ticks;   // 新档:创建时刻 = 现在;时长从零计
        sessionPlaySeconds = 0f;
        sessionClockSeconds = 0f;
        lastPeriodicPersistSeconds = 0f;
        Meta.lastSlotIndex = slotIndex;
        SaveMeta();
        return true;
    }

    /// <summary>读档会话：把该档设为活动档（恢复局内键值缓存与显示数据）。</summary>
    public static bool BeginResumeSession(int slotIndex, SlotData slot)
    {
        if (slotIndex < 0 || slotIndex >= SaveSchema.SlotCount) return false;
        if (slot == null) return false;
        activeSlotIndex = slotIndex;
        activePlaythrough = slot.playthroughNumber;
        runKvCache.Clear();
        if (slot.runKv != null) runKvCache.AddRange(slot.runKv);
        // 显示数据:老档没有 createdTicks(0) 时回填为"上次保存时刻",时长从档内已累计值继续
        sessionCreatedTicks = slot.createdTicks != 0 ? slot.createdTicks : slot.savedAtTicks;
        sessionPlaySeconds = Mathf.Max(0f, slot.playSeconds);
        sessionClockSeconds = 0f;
        lastPeriodicPersistSeconds = 0f;
        Meta.lastSlotIndex = slotIndex;
        SaveMeta();
        return true;
    }

    /// <summary>游玩计时(游玩中每帧调用;暂停时 deltaTime = 0 自然不计)。
    /// 每累计满 PeriodicPersistInterval 秒触发一次周期落盘(见 PeriodicPersist)。</summary>
    public static void TickSessionClock(float deltaTime)
    {
        if (!HasActiveRun || deltaTime <= 0f) return;
        sessionClockSeconds += deltaTime;
        if (sessionClockSeconds - lastPeriodicPersistSeconds >= PeriodicPersistInterval)
            PeriodicPersist();
    }

    /// <summary>
    /// 周期落盘(时长与进度共用同一条节奏):游玩中每满 PeriodicPersistInterval 秒一拍,收局前
    /// (EndRunSession)再补一次 —— 稳定点时写整局快照(时长已含在快照的 playSeconds 里,一并落盘);
    /// 采集条件不满足(过渡 / 玩家未就绪 / 无关卡)时退回纯时长轻量写(见 PersistClockToSlot)。
    /// 每一拍必有写,不空过 —— 直接退出(编辑器 Stop / 关进程 / 崩溃)时长与进度都最多滞后一个间隔。
    /// </summary>
    private static void PeriodicPersist()
    {
        lastPeriodicPersistSeconds = sessionClockSeconds;
        if (!HasActiveRun) return;
        var data = CaptureCurrentSession();   // 内部静默守卫:无关卡 / 非稳定点 / 玩家未就绪 → null
        if (data != null)
        {
            WriteSlot(activeSlotIndex, data);
            return;
        }
        PersistClockToSlot();
    }

    /// <summary>
    /// 纯时长轻量写(PeriodicPersist 稳定点外的降级路径):不采集场景快照,只把"累计时长 + 时刻"
    /// 刷进活动档已有文件 —— 这一拍没有可采现场,时长也不因此丢;场景状态保持最近一次完整
    /// 快照不变(恢复稳定点后由 Settled 事件存档 / 下一拍整快照接上)。
    /// 活动档还没有文件(首张完整快照前)时跳过,不给空档凭空建档;损坏档已被 ReadSlot 隔离
    /// → 返回 null 同样跳过。
    /// </summary>
    private static void PersistClockToSlot()
    {
        if (!HasActiveRun) return;
        string path = SlotPath(activeSlotIndex);
        if (!File.Exists(path)) return;
        var slot = ReadSlot(activeSlotIndex);
        if (slot == null) return;
        slot.playSeconds = sessionPlaySeconds + sessionClockSeconds;
        slot.savedAtTicks = DateTime.UtcNow.Ticks;
        WriteFileAtomic(path, JsonUtility.ToJson(slot));
    }

    /// <summary>收局（返回主菜单 / 结局）：结束活动档会话。</summary>
    public static void EndRunSession()
    {
        PeriodicPersist();   // 收局前把会话最后一段落盘(稳定点整快照 / 已卸载则纯时长;重复写无害,无文件则跳过)
        activeSlotIndex = -1;
        runKvCache.Clear();
        sessionCreatedTicks = 0;
        sessionPlaySeconds = 0f;
        sessionClockSeconds = 0f;
        lastPeriodicPersistSeconds = 0f;
    }

    /// <summary>把活动档标记为"已通关"（结局确认时）：清空局内容,只留通关标记供菜单显示 / 新周目。</summary>
    public static void FinishActiveSlot()
    {
        if (!HasActiveRun) return;
        var slot = new SlotData
        {
            savedAtTicks = DateTime.UtcNow.Ticks,
            createdTicks = sessionCreatedTicks != 0 ? sessionCreatedTicks : DateTime.UtcNow.Ticks,
            playSeconds = sessionPlaySeconds + sessionClockSeconds,
            playthroughNumber = activePlaythrough,
            runFinished = true,
        };
        WriteSlot(activeSlotIndex, slot);
    }

    // ==================== 采集（写盘前一刻从现场镜像） ====================

    /// <summary>
    /// 自动存档到活动档（开局到达 / 每关到达站稳 / 返回主菜单前）。只在稳定点成功；
    /// 未就绪（过渡中 / 玩家未加载）返回 false，由调用方决定是否提示。
    /// </summary>
    public static bool SaveActiveSlot()
    {
        if (!HasActiveRun)
        {
            Debug.LogWarning("[SaveSystem] 没有活动档，跳过自动存档");
            LastSaveSucceeded = false;
            return false;
        }
        var data = CaptureCurrentSession();
        if (data == null)
        {
            Debug.LogWarning("[SaveSystem] 当前不在可存档状态（稳定点外 / 玩家未就绪），跳过自动存档");
            LastSaveSucceeded = false;
            return false;
        }
        WriteSlot(activeSlotIndex, data);
        LastSaveSucceeded = true;
        return true;
    }

    /// <summary>
    /// 从当前现场采集整局快照（关卡全量物件状态 + 玩家）。要求 LevelTransitionManager 存在、
    /// 处于稳定点（IsSettled）、玩家场景已加载；任一不满足返回 null。
    /// </summary>
    public static SlotData CaptureCurrentSession()
    {
        var ltm = LevelTransitionManager.Instance;
        if (ltm == null) return null;
        if (!ltm.IsSettled) return null;
        if (!ltm.IsPlayerSceneLoaded) return null;

        Scene scene = ltm.CurrentLevelScene;
        if (!scene.IsValid() || !scene.isLoaded)
        {
            Debug.LogWarning("[SaveSystem] 当前无已加载关卡，无法采集存档");
            return null;
        }

        var slot = new SlotData
        {
            savedAtTicks = DateTime.UtcNow.Ticks,
            createdTicks = sessionCreatedTicks != 0 ? sessionCreatedTicks : DateTime.UtcNow.Ticks,
            playSeconds = sessionPlaySeconds + sessionClockSeconds,
            playthroughNumber = activePlaythrough,
            levelPath = scene.path,
        };
        slot.runKv = new List<KvEntry>(runKvCache);

        // a) 关卡场景全量可存档物件（ISceneSaveable 类型封闭：核心不感知具体类型）
        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var saveable in root.GetComponentsInChildren<ISceneSaveable>(true))
            {
                string data = saveable.CaptureToJson();
                if (string.IsNullOrEmpty(data)) continue;   // 非拾取类 Interactable 等:无状态可存
                // ISceneSaveable 的实现方都是组件;身份路径经 Component 取 transform
                var comp = saveable as Component;
                slot.sceneState.Add(new SaveableEntry
                {
                    type = saveable.SaveableType,
                    path = comp != null ? TransformPath.GetPath(comp.transform) : "",
                    data = data,
                });
            }
        }

        // b) 玩家快照（控制器 / 刚体直读,不经 PlayerStateSO 镜像 —— 采集时机要"当下"，不依赖镜像新鲜度）
        var player = GameObject.FindGameObjectWithTag("Player");
        if (player != null)
        {
            var flight = player.GetComponent<BeeFlightController>();
            var interaction = player.GetComponent<BeeInteractionController>();
            var rb = player.GetComponent<Rigidbody>();
            if (flight != null && interaction != null && rb != null)
            {
                slot.player.position = rb.position;
                slot.player.rotation = rb.rotation;
                slot.player.localScale = player.transform.localScale;
                slot.player.velocity = rb.velocity;
                slot.player.beeState = (int)flight.CurrentState;
                slot.player.stamina = flight.Stamina;
                slot.player.maxStamina = flight.MaxStamina;
                slot.player.lookRotation = flight.CameraRotation;
                slot.player.isHolding = interaction.IsHolding;
                slot.player.heldObjectPath = interaction.IsHolding && interaction.HeldObject != null
                    ? TransformPath.GetPath(interaction.HeldObject)
                    : "";
                slot.player.heldItemId = interaction.HeldItemId;
            }
        }

        return slot;
    }

    // ==================== 恢复（读档；流程在 BeginResume 之后调用） ====================

    /// <summary>
    /// 恢复一档（协程：场景物件 → 玩家 → 持物重挂）。要求关卡场景已由 BeginResume 加载。
    /// 恢复期间 IsSettled = false —— 刷卡被读卡器 / 管理器静默拒绝，读卡区里的卡不会自动重刷。
    /// 完成后由流程调 LevelTransitionManager.SettleAfterRestore() 进入稳定点。
    /// </summary>
    public static IEnumerator ApplyRestoreRoutine(SlotData slot)
    {
        var ltm = LevelTransitionManager.Instance;
        if (ltm == null) yield break;
        Scene scene = ltm.CurrentLevelScene;
        if (!scene.IsValid() || !scene.isLoaded)
        {
            Debug.LogError("[SaveSystem] 恢复失败：关卡场景未就绪");
            yield break;
        }

        // a) 场景物件状态（先于任何游玩帧 —— SlidingDoor 的滑门基线 = 场景摆好的关闭位）
        if (slot.sceneState != null)
        {
            foreach (var entry in slot.sceneState)
            {
                var target = TransformPath.FindByPath(scene, entry.path);
                if (target == null)
                {
                    Debug.LogWarning($"[SaveSystem] 存档条目 {entry.type}@{entry.path} 在 {scene.name} 中找不到对应物体，已跳过（物体改名 / 被删）");
                    continue;
                }
                // 同一物体可能挂多个可存档组件（如柴油机 = 交互类型标记 Interactable + 自身行为脚本）：
                // 按存档条目里的类型找对应的那一个 —— 采集侧就是"有几个组件写几条"，恢复侧必须一一对上，
                // 不能靠 GetComponent 返回哪个（顺序变了就静默恢复不了）
                ISceneSaveable saveable = null;
                foreach (var candidate in target.GetComponents<ISceneSaveable>())
                {
                    if (candidate.SaveableType != entry.type) continue;
                    saveable = candidate;
                    break;
                }
                if (saveable == null)
                {
                    Debug.LogWarning($"[SaveSystem] 存档条目 {entry.type}@{entry.path} 与物体现有组件不符，已跳过（组件被换？）");
                    continue;
                }
                saveable.RestoreFromJson(entry.data);
            }
        }
        Physics.SyncTransforms();   // 刚体拾取物归位后立即同步物理变换
        yield return null;          // 给物理同步 / 组件启动一帧

        // b) 玩家位姿 + 持物
        var player = GameObject.FindGameObjectWithTag("Player");
        if (player != null)
        {
            var flight = player.GetComponent<BeeFlightController>();
            var interaction = player.GetComponent<BeeInteractionController>();
            if (flight != null)
                flight.RestoreFromSnapshot(slot.player);

            if (interaction != null)
            {
                interaction.ForceDrop();   // 防御:恢复前清空手里的(场景全新实例本应无持物)
                if (slot.player.isHolding && !string.IsNullOrEmpty(slot.player.heldObjectPath))
                {
                    var held = TransformPath.FindByPath(scene, slot.player.heldObjectPath);
                    if (held != null)
                        interaction.ForceHold(held);
                    else
                        Debug.LogWarning($"[SaveSystem] 存档持物 {slot.player.heldObjectPath} 已不在 {scene.name} 中，本次读档不重挂（物品留在地图原处）");
                }
            }
        }
        else
        {
            Debug.LogWarning("[SaveSystem] 恢复玩家失败：找不到带 Player 标签的玩家（玩家场景未加载？）");
        }
        yield return null;
    }

    // ==================== 槽位文件层 ====================

    /// <summary>槽位摘要（主菜单显示:周目 / 关名 / 时间 / 是否已通关）。</summary>
    public static SlotSummary DescribeSlot(int index)
    {
        var summary = new SlotSummary();
        var slot = ReadSlot(index);
        if (slot == null) return summary;
        summary.exists = true;
        summary.runFinished = slot.runFinished;
        summary.playthroughNumber = slot.playthroughNumber;
        summary.levelPath = slot.levelPath;
        summary.savedAtTicks = slot.savedAtTicks;
        summary.createdTicks = slot.createdTicks != 0 ? slot.createdTicks : slot.savedAtTicks;
        summary.playSeconds = slot.playSeconds;
        return summary;
    }

    public static bool SlotExists(int index)
    {
        if (index < 0 || index >= SaveSchema.SlotCount) return false;
        return File.Exists(SlotPath(index));
    }

    /// <summary>读档（损坏 → 改名 .corrupt 并警告，返回 null 视为空档）。</summary>
    public static SlotData ReadSlot(int index)
    {
        if (index < 0 || index >= SaveSchema.SlotCount) return null;
        string path = SlotPath(index);
        if (!File.Exists(path)) return null;
        try
        {
            var slot = JsonUtility.FromJson<SlotData>(File.ReadAllText(path));
            if (slot == null) throw new InvalidDataException("JSON 为空或结构损坏");
            return slot;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SaveSystem] 槽 {index} 存档损坏，已改名 {path}.corrupt（原档保留供排查）: {e.Message}");
            try { File.Delete(path + ".corrupt"); } catch (Exception) { }
            try { File.Move(path, path + ".corrupt"); } catch (Exception) { }
            return null;
        }
    }

    /// <summary>写档（覆盖；原子写防崩溃半写）。</summary>
    public static void WriteSlot(int index, SlotData slot)
    {
        if (index < 0 || index >= SaveSchema.SlotCount) return;
        slot.savedAtTicks = DateTime.UtcNow.Ticks;
        slot.playthroughNumber = activePlaythrough;
        WriteFileAtomic(SlotPath(index), JsonUtility.ToJson(slot));
    }

    /// <summary>清空槽（同时清理"最后游玩槽"指向：清掉的正是它时回退为无）。</summary>
    public static void ClearSlot(int index)
    {
        if (index < 0 || index >= SaveSchema.SlotCount) return;
        try
        {
            if (File.Exists(SlotPath(index))) File.Delete(SlotPath(index));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SaveSystem] 清空槽 {index} 失败：{e.Message}");
        }
        if (Meta.lastSlotIndex == index && !SlotExists(index))
        {
            Meta.lastSlotIndex = -1;
            SaveMeta();
        }
    }

    // === 路径与 KV 小工具 ===

    private static string SavesDirPath()
    {
        string dir = Path.Combine(Application.persistentDataPath, "Saves");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return dir;
    }

    private static string MetaPath() => Path.Combine(SavesDirPath(), "meta.json");

    private static string SlotPath(int index) => Path.Combine(SavesDirPath(), $"slot{index}.json");

    private static void WriteFileAtomic(string path, string content)
    {
        try
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            try
            {
                File.Replace(tmp, path, null);   // 同卷原子替换
            }
            catch (Exception)
            {
                File.Delete(path);
                File.Move(tmp, path);            // Replace 不可用(跨卷等)时退化
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[SaveSystem] 写盘失败：{path} — {e.Message}");
        }
    }

    private static bool FindKv(List<KvEntry> list, string key, out KvEntry entry)
    {
        entry = null;
        if (list == null || string.IsNullOrEmpty(key)) return false;
        for (int i = 0; i < list.Count; i++)
            if (list[i].key == key) { entry = list[i]; return true; }
        return false;
    }

    private static KvEntry GetOrCreateKv(List<KvEntry> list, string key)
    {
        if (FindKv(list, key, out var entry)) return entry;
        entry = new KvEntry { key = key };
        list.Add(entry);
        return entry;
    }
}
