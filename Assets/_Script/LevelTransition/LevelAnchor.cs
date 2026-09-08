using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// 关卡锚点标记：标记本关卡"入口门" / "出口门"的位置与朝向，是传送门（PortalDoor）的位姿基准。
/// - 出口锚点（Exit）：标记本关出口门洞的位置；同时可引用出口门上的 SlidingDoor
///   （Room_01 的门是 FBX 实例，无法重挂父子关系，所以用显式引用；无引用时管理器
///   自动在锚点子物体中查找 SlidingDoor）。出口锚点上可挂 PortalDoor（穿门组件）。
/// - 入口锚点（Entry）：标记下一关门洞的位置；传送门把玩家相对出口锚点的位姿映射到
///   入口锚点，生成穿门落点与门面渲染相机的位姿参照。
///   注意：入口锚点只做位姿参照 —— 不挂 PortalDoor、不摆门面平面 / 相机。
///   门面显示(纹理 + 相机)只属于出口锚点一侧(玩家看过去的那扇门)。
///
/// 出口"刷卡后去哪"不在锚点上配置 —— 目的地由刷卡钥匙(卡片)携带(见 Card.cs 头注释)，
/// 刷卡瞬间由 LevelTransitionManager 从卡读取。本锚点只管：门在哪个门洞、位姿基准与穿门落点。
///
/// 作者约定（朝向语义，两侧必须成对正确，否则穿门方向/门面纹理就反）：
///   - 两关锚点 +Z 都沿玩家"连续前进"的方向：出口锚点 +Z 指向门洞外侧（穿门离开本关的方向，
///     玩家从 -Z 侧走向门）；入口锚点 +Z 指向目标关的纵深（进门后继续走的方向）。
///     多数相邻两关 = 两锚点世界朝向相同（都朝同一条前进路径）；摆法是否对，Play 里走一遍即知：
///     穿过瞬间应正对目标关纵深、门面纹理应朝玩家来路可见。
///   - 常见错误：把某一侧锚点转反（如出口锚点 +Z 指向房间内）→ 门面平面背对玩家不可见，
///     穿门落点/朝向也反。
///   - 锚点放在门洞正中，localScale 保持 1。
/// 注：玩家出生 / 无门转换的落点在 PlayerSpawnPoint 处理（开局 BeginRun 与带落点的直达
/// RequestDirectSwitch 会落到目标关默认出生点，见 LevelTransitionManager.PlayerLanding）；
/// 入口 / 出口锚点仍只管门演出与穿门落点，两者不重叠。
/// </summary>
public class LevelAnchor : MonoBehaviour
{
    public enum AnchorType { Entry, Exit }

    /// <summary>刷卡钥匙携带的出口去向(None = 未配置 —— 刷出口门直接报错拒绝,强制显式配置)。
    /// 本枚举定义在此处供卡片(Card)复用;LinearNext = 关卡列表线性下一关。</summary>
    public enum DestinationKind
    {
        None,
        LinearNext,
        Scene,
        Ending,
    }

    [SerializeField, Tooltip("锚点类型：Entry=入口（传送门位姿基准），Exit=出口")]
    private AnchorType type = AnchorType.Entry;

    [SerializeField, Tooltip("可选：出口门上的 SlidingDoor（门是 FBX 实例等无法重挂父子时用显式引用）")]
    private SlidingDoor exitDoor;

    public AnchorType Type => type;

    /// <summary>出口门的 SlidingDoor：显式引用优先，缺省时在锚点子物体中查找。</summary>
    public SlidingDoor ResolveExitDoor()
    {
        if (exitDoor != null) return exitDoor;
        return GetComponentInChildren<SlidingDoor>(true);
    }

    /// <summary>在指定场景中查找第一个指定类型的锚点（入口每关一个；兼容旧调用）。</summary>
    public static Transform FindAnchor(Scene scene, AnchorType targetType)
    {
        foreach (var anchor in FindAllAnchors(scene))
            if (anchor.Type == targetType)
                return anchor.transform;
        return null;
    }

    /// <summary>在指定场景中查找全部指定类型的锚点（出口可多道：每道门各有目的地）。</summary>
    public static List<LevelAnchor> FindAllAnchors(Scene scene, AnchorType targetType)
    {
        var result = new List<LevelAnchor>();
        foreach (var anchor in FindAllAnchors(scene))
            if (anchor.Type == targetType)
                result.Add(anchor);
        return result;
    }

    private static List<LevelAnchor> FindAllAnchors(Scene scene)
    {
        var result = new List<LevelAnchor>();
        if (!scene.IsValid() || !scene.isLoaded) return result;
        foreach (var root in scene.GetRootGameObjects())
            result.AddRange(root.GetComponentsInChildren<LevelAnchor>(true));
        return result;
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        // 锚点朝向箭头：+Z = 穿越方向（Entry 绿 / Exit 橙）。两关锚点朝向一致是传送数学正确的前提。
        Color c = type == AnchorType.Entry ? new Color(0.2f, 1f, 0.5f, 0.9f) : new Color(1f, 0.55f, 0.1f, 0.9f);
        Gizmos.color = c;
        Vector3 tip = transform.position + transform.forward * 1.2f;
        Gizmos.DrawLine(transform.position, tip);
        Gizmos.DrawSphere(tip, 0.08f);
        Gizmos.DrawWireSphere(transform.position, 0.15f);

        // 约定警告：锚点 localScale 必须为 1（传送数学依赖），缩放后画红框提醒
        Vector3 s = transform.lossyScale;
        if (!Mathf.Approximately(s.x, 1f) || !Mathf.Approximately(s.y, 1f) || !Mathf.Approximately(s.z, 1f))
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireCube(transform.position, Vector3.one * 0.6f);
        }

        Handles.Label(transform.position + Vector3.up * 0.4f,
            $"{gameObject.name} · {(type == AnchorType.Entry ? "Entry" : "Exit")} · +Z 穿越方向");
    }
#endif
}
