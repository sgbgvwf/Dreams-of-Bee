using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// 卡片:可拾取的刷卡钥匙(IDoorKey 的第一个实现) + 出口目的地数据。
///
/// 是什么:一张"能开门的卡"。进读卡器触发区 = 刷卡成功(读卡器负责归属校验);
/// 刷的是出口门时,【刷卡后去哪 = 本卡携带的目的地】—— 刷卡瞬间由 LevelTransitionManager
/// 从卡读取并执行。同一扇出口门,刷不同卡 = 去不同地方(最近一次刷卡生效)。
///
/// 目的地配置(必填,刷出口门时):选中卡 → 本组件填 Destination Kind ——
///   Scene = 下方拖目标关卡场景(须已加入 Build Settings):刷卡即后台加载,加载完门开,穿门传送;
///   Ending = 下方填结局 id(见 EndingCatalog):目的地不是关卡而是"结局房间"
///     (目录里配的 scenePath),装载演出与 Scene 一模一样 —— 刷卡即进结局态,门开,玩家穿门被
///     传送进那个纯色房间,在那里演完结局(见 EndingRoom);
///   不配(None)/配了缺目标 → 刷出口门时报错拒绝 —— 这是刻意的:目的地只认卡,
///   每张会刷出口门的卡都必须显式声明去向(关卡图非线性,没有"列表下一关"这种默认值)。
///   刷普通门(非出口)时用不到目的地,可留空。
///   结局没登记 / 未解锁同样是刷卡当场拒绝(见 DiagnosticHint)——不留"门开了却到不了结局"的死局。
///
/// 其余行为(拾取/触发/身份)不变:Card extends Interactable(Pickup);
/// 身份 = KeyId(Interactable.itemId),读卡器 requiredItemId 可选配对;
/// 无归属关卡概念 —— 关卡物件随穿门卸载销毁,卡带不出关,刷卡去哪由本卡目的地决定。
/// </summary>
public class Card : Interactable, IDoorKey
{
    [Header("出口目的地(刷出口门时必配)")]
    [SerializeField, Tooltip("刷卡后去哪:Scene=下方指定目标关卡;Ending=进入结局(填结局 id,目的地 = 结局目录里配的结局房间)。两者装载演出完全相同:加载 → 开门 → 穿门传送 → 卸载前一关。None(默认)= 未配置,刷出口门会报错拒绝 —— 目的地唯一权威是卡,没有兜底(关卡图非线性,没有‘列表下一关’可默认)。")]
    private LevelAnchor.DestinationKind destinationKind = LevelAnchor.DestinationKind.None;

#if UNITY_EDITOR
    [SerializeField, Tooltip("destinationKind = Scene 时的目标关卡场景（须已加入 Build Settings）")]
    private SceneAsset destinationScene;
#endif

    [SerializeField, HideInInspector]
    private string destinationScenePath;

    [SerializeField, Tooltip("destinationKind = Ending 时的结局 id（见 EndingCatalog，如 \"ending_demo\"）")]
    private string destinationEndingId = "";

    /// <summary>钥匙身份 Id = Interactable.itemId（读卡器 requiredItemId 配对用；空 = 不校验）。</summary>
    public string KeyId => ItemId;

    /// <summary>本卡携带的出口去向（None = 未配置）。</summary>
    public LevelAnchor.DestinationKind DestinationKind => destinationKind;

    /// <summary>destinationKind = Scene 时的目标场景路径（未拖 = 空）。</summary>
    public string DestinationScenePath => destinationScenePath;

    /// <summary>destinationKind = Ending 时的结局 id（未填 = 空）。</summary>
    public string DestinationEndingId => destinationEndingId;

    /// <summary>配置缺失时给人看的话(管理器报错日志直接用、刷卡当场拒绝):未配置/Scene 没拖/
    /// Ending 没填/结局没登记/结局未解锁。目的地不可用的卡一律在刷卡瞬间被挡下 ——
    /// 不能等玩家推开门、穿过门面才发现到不了。</summary>
    public string DiagnosticHint
    {
        get
        {
            if (destinationKind == LevelAnchor.DestinationKind.None)
                return $"[Card] {name}: 没有配置目的地（Destination Kind = None）—— 刷出口门需要它。请在 Card 上配置 Destination Kind(Scene=拖目标关卡 / Ending=填结局 id)";
            if (destinationKind == LevelAnchor.DestinationKind.Scene && string.IsNullOrEmpty(destinationScenePath))
                return $"[Card] {name}: Destination Kind = Scene 但没有拖目标场景 —— 请在 Card 上把目标关卡拖进 Destination Scene";
            if (destinationKind == LevelAnchor.DestinationKind.Ending)
            {
                if (string.IsNullOrEmpty(destinationEndingId))
                    return $"[Card] {name}: Destination Kind = Ending 但没有填结局 id —— 请在 Card 上填写 Destination Ending Id(见 EndingCatalog)";
                if (EndingCatalog.Find(destinationEndingId) == null)
                    return $"[Card] {name}: 结局 [{destinationEndingId}] 未在 EndingCatalog 登记 —— 加结局 = 在 EndingCatalog.RegisterDefaultEndings 里加一条(或换成本卡填的 id)";
                if (!EndingCatalog.CanTrigger(destinationEndingId))
                    return $"[Card] {name}: 结局 [{destinationEndingId}] 尚未解锁（requiresEnding / unlockCheck 未满足）—— 达成前置结局后再来刷这张卡";
            }
            return "";
        }
    }

    /// <summary>卡片的交互类型恒为拾取。</summary>
    public override InteractionType Type => InteractionType.Pickup;

#if UNITY_EDITOR
    private void OnValidate()
    {
        // 目标场景镜像为路径(运行时只走路径加载;与 LevelTransitionManager.levels 同一模式)
        if (destinationScene != null)
        {
            destinationScenePath = AssetDatabase.GetAssetPath(destinationScene);
            if (!IsInBuildSettings(destinationScenePath))
                Debug.LogWarning($"[Card] {name} 的目的地 {destinationScenePath} 未加入 Build Settings(File → Build Settings → Scenes in Build),刷卡时将无法加载", this);
        }
        else if (destinationKind == LevelAnchor.DestinationKind.Scene)
        {
            destinationScenePath = "";
            Debug.LogWarning($"[Card] {name} 的 Destination Kind = Scene 但未拖入目标场景,刷卡时会报错拒绝。", this);
        }
    }

    private static bool IsInBuildSettings(string path)
    {
        foreach (var s in EditorBuildSettings.scenes)
            if (s.path == path) return true;
        return false;
    }
#endif
}
