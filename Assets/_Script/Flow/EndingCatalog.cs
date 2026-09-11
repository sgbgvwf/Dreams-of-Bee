using System;
using System.Collections.Generic;

/// <summary>
/// 结局目录(扩展口 B —— 加结局 = 目录里加一条 + 搭一个结局房间,流程零改动)。
///
/// 每条结局 = EndingDefinition: id(触发用) + 演出场景 + 解锁条件。
/// 【文案与画面都不在这里】—— 结局在一个专属场景里演(结局房间):玩家被门传送进去、
/// 什么都看不见(全屏纯色)、不允许交互,字幕在场景里逐条播(见 EndingRoom)。
/// 本目录只管"有哪些结局、去哪个场景、怎么解锁"。
/// 触发两条路:
///   - 有出口门配卡:卡上 DestinationKind = Ending + 填本目录的 id(纯 Inspector 配置,零代码)——
///     整套走普通门的演出(加载结局场景 → 开门 → 玩家穿过门面被传送进去 → 卸载前一关),
///     详见 LevelTransitionManager.AdvanceTransition;
///   - 内容代码任意时刻调 GameFlowManager.TriggerEnding(id)(典型挂最终关某个 IInteractable):
///     无门直达换场景把玩家送进结局房间。
/// 收尾:结局房间演完 → 回主菜单,并【删掉这一局的活动档】;跨局元数据照记
/// (completedRuns+1 / 结局入 seenEndings,见 GameFlowManager.ConfirmEnding)。
///
/// 解锁条件(多结局解锁关系):requiresEnding 需要先达成指定结局(跨局 seenEndings 判);
/// unlockCheck 为可选的附加条件回调(读 SaveSystem 全局/局内 KV 做"达成 A 才解锁 B"等)。
/// 触发时条件不满足:警告日志 + 拒绝(正常内容不会去触发锁定的结局,这是防误触发的保险);
/// 刷卡路径上更早 —— 卡自己的 DiagnosticHint 在刷卡当场就把未登记的 id 挡下。
/// </summary>
public static class EndingCatalog
{
    /// <summary>一条结局的登记项(文字与画面在结局房间场景里,不在这)。</summary>
    public sealed class EndingDefinition
    {
        public string id;
        /// <summary>
        /// 结局演出场景(结局房间)的路径,必配 —— 玩家会被传送进这个场景里演完结局。
        /// 要求:场景里的物件全是纯色(玩家什么都看不见)、有 Entry 锚点(落点)与门面渲染相机、
        /// 挂 EndingRoom 组件;必须已加入 Build Settings。空 = 刷卡当场被拒(见
        /// LevelTransitionManager.RequestExitKeyedCore)。注意 Room_00 只做主菜单背景,不要拿它当结局房间。
        /// </summary>
        public string scenePath = "";
        /// <summary>需先达成的结局 id(跨局;空 = 无前置结局)。</summary>
        public string[] requiresEnding;
        /// <summary>附加解锁条件(读 SaveSystem KV / 结局列表;空 = 无条件)。</summary>
        public Func<bool> unlockCheck;
    }

    /// <summary>运行时锁定只读目录。</summary>
    private static readonly Dictionary<string, EndingDefinition> registry = new Dictionary<string, EndingDefinition>();

    static EndingCatalog()
    {
        RegisterDefaultEndings();
    }

    /// <summary>
    /// 作者目录(新结局 = 在这里加一条 Register,同时搭好对应的结局房间场景)。
    /// 每条先达成的结局会入 meta.seenEndings,二周目内容直接用 SaveSystem.IsEndingUnlocked(id) 查询。
    /// </summary>
    private static void RegisterDefaultEndings()
    {
        // 结局 1(在结局房间里那一套 stage 上配成纯白)
        Register(new EndingDefinition
        {
            id = "ending_1",
            scenePath = "Assets/Scene/Room_Ending.unity",
            requiresEnding = null,   // 两条结局各自独立可达,没有先后关系
        });

        // 结局 2(同一个结局房间,那一套 stage 上配成纯黑;要各用各的房间就各填各的 scenePath)
        Register(new EndingDefinition
        {
            id = "ending_2",
            scenePath = "Assets/Scene/Room_Ending.unity",
            requiresEnding = null,   // 两条结局各自独立可达,没有先后关系
        });
    }

    /// <summary>登记一条结局(id 重复 = 警告并覆盖)。</summary>
    public static void Register(EndingDefinition def)
    {
        if (def == null || string.IsNullOrEmpty(def.id)) return;
        registry[def.id] = def;
    }

    /// <summary>按 id 查结局定义(未登记返回 null)。</summary>
    public static EndingDefinition Find(string endingId)
    {
        if (string.IsNullOrEmpty(endingId)) return null;
        registry.TryGetValue(endingId, out var def);
        return def;
    }

    /// <summary>触发前校验:定义存在且解锁条件满足。</summary>
    public static bool CanTrigger(string endingId)
    {
        var def = Find(endingId);
        return def != null && IsUnlocked(def);
    }

    private static bool IsUnlocked(EndingDefinition def)
    {
        if (def.requiresEnding != null)
            foreach (var need in def.requiresEnding)
                if (!SaveSystem.IsEndingUnlocked(need))
                    return false;
        if (def.unlockCheck != null && !def.unlockCheck())
            return false;
        return true;
    }
}
