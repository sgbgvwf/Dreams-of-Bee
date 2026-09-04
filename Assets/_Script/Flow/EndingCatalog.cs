using System;
using System.Collections.Generic;

/// <summary>
/// 结局目录(扩展口 B —— 加结局 = 目录里加一条,播放层与流程零改动)。
///
/// 每条结局 = EndingDefinition: id(触发用) + 演出文案 + 可选演出背景场景 + 解锁条件。
/// 触发两条路:
///   - 内容代码任意时刻调 GameFlowManager.TriggerEnding(id)(典型挂最终关某个 IInteractable);
///   - 关卡出口门配 LevelAnchor.DestinationKind = Ending + 填本目录的 id(纯 Inspector 配置,零代码)。
/// 演出:默认不加载场景 —— EndingOverlay 自带纯色幕(注意:Room_00 只做主菜单背景,不担任结局演出地);
/// 只有定义显式给了 backdropScenePath(专属演出场景)时才加载它。确认离开时自动完成
/// completedRuns+1 / 结局入 seenEndings / 槽位置"已通关"(见 GameFlowManager.EndingConfirmed)。
///
/// 解锁条件(多结局解锁关系):requiresEnding 需要先达成指定结局(跨局 seenEndings 判);
/// unlockCheck 为可选的附加条件回调(读 SaveSystem 全局/局内 KV 做"达成 A 才解锁 B"等)。
/// 触发时条件不满足:警告日志 + 拒绝(正常内容不会去触发锁定的结局,这是防误触发的保险)。
/// </summary>
public static class EndingCatalog
{
    /// <summary>结局文案(含默认演出背景;中文文案内联,作者直接改这里)。</summary>
    public sealed class EndingDefinition
    {
        public string id;
        public string title;
        public string body;
        /// <summary>
        /// 演出背景场景路径(空 = 纯色结局幕,默认;非空 = 加载该场景做演出背景,需作者自配相机。
        /// 注意 Room_00 只做主菜单背景,不要拿它当结局场景)。
        /// </summary>
        public string backdropScenePath = "";
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
    /// 作者目录(新结局 = 在这里加一条 Register)。每条先达成的结局会入 meta.seenEndings,
    /// 二周目内容直接用 SaveSystem.IsEndingUnlocked(id) 查询。
    /// </summary>
    private static void RegisterDefaultEndings()
    {
        // 示例结局(验证流程用;作者正式结局照此加条目即可)
        Register(new EndingDefinition
        {
            id = "ending_demo",
            title = "示例结局 · 归巢",
            body = "蜂群收拢翅膀,梦境在灯影里合拢。\n\n—— 旅程告一段落,你的故事将被记住。",
            requiresEnding = null,
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
