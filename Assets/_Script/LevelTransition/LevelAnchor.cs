using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// 关卡锚点标记：标记本关卡"入口门" / "出口门"的位置与朝向，是传送门（PortalDoor）的位姿基准。
/// - 出口锚点（Exit）：标记本关出口门洞的位置；同时可引用出口门上的 SlidingDoor
///   （Room_01 的门是 FBX 实例，无法重挂父子关系，所以用显式引用；无引用时管理器
///   自动在锚点子物体中查找 SlidingDoor）。出口锚点上可挂 PortalDoor（传送门控制器）。
/// - 入口锚点（Entry）：标记下一关门洞的位置；传送门把玩家相对出口锚点的位姿映射到
///   入口锚点，生成门面渲染相机姿态与传送落点。
/// 作者约定：锚点放在门洞正中，+Z = 穿越方向，localScale 保持 1。
/// 注：玩家出生不在这里处理——出生位置固定在 Player 场景的 Player 对象上（直接拖它即可），
/// 只在开局用一次，不占用关卡/门系统的锚点机制。
/// </summary>
public class LevelAnchor : MonoBehaviour
{
    public enum AnchorType { Entry, Exit }

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

    /// <summary>在指定场景中查找指定类型的锚点（锚点是传送门的位姿基准）。</summary>
    public static Transform FindAnchor(Scene scene, AnchorType targetType)
    {
        foreach (var root in scene.GetRootGameObjects())
        {
            var anchor = root.GetComponentInChildren<LevelAnchor>(true);
            if (anchor != null && anchor.Type == targetType)
                return anchor.transform;
        }
        return null;
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
