using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 关卡锚点标记：标记本关卡"入口门" / "出口门"的位置。
/// - 入口锚点（Entry）：新关卡加载后，LevelTransitionManager 会把该关场景根整体移动，
///   使入口锚点与世界空间中当前关的出口锚点完全重合 → 玩家穿过门时世界坐标天然连续。
/// - 出口锚点（Exit）：标记本关出口门的位置；同时可引用出口门上的 SlidingDoor
///   （Room_01 的门是 FBX 实例，无法重挂父子关系，所以用显式引用；无引用时管理器
///   自动在锚点子物体中查找 SlidingDoor）。
/// 锚点应放在"关卡对齐根"（含全部关卡内容的场景根）的子层级中，localScale 保持 1。
/// </summary>
public class LevelAnchor : MonoBehaviour
{
    public enum AnchorType { Entry, Exit }

    [SerializeField, Tooltip("锚点类型：Entry=入口（对齐基准），Exit=出口")]
    private AnchorType type = AnchorType.Entry;

    [SerializeField, Tooltip("可选：入口锚点上的门模型，对齐完成后由管理器禁用（避免与当前关出口门重叠闪烁）")]
    private GameObject entryDoorModel;

    [SerializeField, Tooltip("可选：出口门上的 SlidingDoor（门是 FBX 实例等无法重挂父子时用显式引用）")]
    private SlidingDoor exitDoor;

    public AnchorType Type => type;
    public GameObject EntryDoorModel => entryDoorModel;

    /// <summary>出口门的 SlidingDoor：显式引用优先，缺省时在锚点子物体中查找。</summary>
    public SlidingDoor ResolveExitDoor()
    {
        if (exitDoor != null) return exitDoor;
        return GetComponentInChildren<SlidingDoor>(true);
    }

    /// <summary>在指定场景中查找指定类型的锚点（锚点是关卡对齐根的子物体）。</summary>
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

    /// <summary>
    /// 返回包含 Entry 锚点的场景根变换 —— 即"关卡对齐根"。
    /// 新关卡加载后由管理器整体移动该根，使入口锚点与当前关出口锚点重合。
    /// </summary>
    public static Transform FindLevelRoot(Scene scene)
    {
        foreach (var root in scene.GetRootGameObjects())
        {
            foreach (var anchor in root.GetComponentsInChildren<LevelAnchor>(true))
                if (anchor.Type == AnchorType.Entry)
                    return root.transform;
        }
        return null;
    }
}
