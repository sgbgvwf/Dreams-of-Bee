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
///   自动在锚点子物体中查找 SlidingDoor）。出口锚点上可挂 PortalDoor（传送门控制器）。
/// - 入口锚点（Entry）：标记下一关门洞的位置；传送门把玩家相对出口锚点的位姿映射到
///   入口锚点，生成门面渲染相机姿态与传送落点。
///
/// 出口目的地（关卡图扩展口，单向推进）：每道出口门独立声明"刷卡通过后去哪"——
///   - LinearNext(默认)：关卡列表的线性下一关（今日行为，Room_01/02 零改动）；
///   - Scene：任意指定关卡（分支 / 非线性路径；场景须已加入 Build Settings）；
///   - Ending：不加载关卡，通知流程进入结局（结局在 EndingCatalog 登记，演出默认 Room_00）。
/// 作者约定：锚点放在门洞正中，+Z = 穿越方向，localScale 保持 1。
/// 注：玩家出生不在这里处理——出生位置固定在 Player 场景的 Player 对象上（直接拖它即可），
/// 只在开局用一次，不占用关卡/门系统的锚点机制。
/// </summary>
public class LevelAnchor : MonoBehaviour
{
    public enum AnchorType { Entry, Exit }

    /// <summary>出口门刷卡通过后的去向（LinearNext = 列表线性下一关，默认）。</summary>
    public enum DestinationKind
    {
        LinearNext,
        Scene,
        Ending,
    }

    [SerializeField, Tooltip("锚点类型：Entry=入口（传送门位姿基准），Exit=出口")]
    private AnchorType type = AnchorType.Entry;

    [SerializeField, Tooltip("可选：出口门上的 SlidingDoor（门是 FBX 实例等无法重挂父子时用显式引用）")]
    private SlidingDoor exitDoor;

    // === 出口目的地配置(只对 Exit 锚点有意义;Entry 保持默认 LinearNext 即可) ===
    [SerializeField, Tooltip("刷卡通过后的去向：LinearNext=列表下一关(默认)；Scene=下方指定场景；Ending=触发结局(填结局 id)")]
    private DestinationKind destinationKind = DestinationKind.LinearNext;

#if UNITY_EDITOR
    [SerializeField, Tooltip("destinationKind = Scene 时的目标关卡场景（须已加入 Build Settings）")]
    private SceneAsset destinationScene;
#endif

    [SerializeField, HideInInspector]
    private string destinationScenePath;

    [SerializeField, Tooltip("destinationKind = Ending 时的结局 id（见 EndingCatalog，如 \"ending_demo\"）")]
    private string endingId = "";

    public AnchorType Type => type;
    public DestinationKind Destination => destinationKind;
    /// <summary>目标关卡场景路径（destinationKind = Scene 时有效；空 = 未配置）。</summary>
    public string DestinationScenePath => destinationScenePath;
    /// <summary>结局 id（destinationKind = Ending 时有效）。</summary>
    public string EndingId => endingId;

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
    private void OnValidate()
    {
        // 目标场景镜像为路径(与 LevelTransitionManager.levels 同一模式:运行时只走路径加载)
        if (destinationScene != null)
        {
            destinationScenePath = AssetDatabase.GetAssetPath(destinationScene);
            if (destinationScenePath != gameObject.scene.path && !IsInBuildSettings(destinationScenePath))
                Debug.LogWarning($"[LevelAnchor] 出口目的地 {destinationScenePath} 未加入 Build Settings(File → Build Settings → Scenes in Build),运行时无法加载", this);
        }
        else if (destinationKind == DestinationKind.Scene)
        {
            destinationScenePath = "";
            Debug.LogWarning($"[LevelAnchor] {name} 的 destinationKind = Scene 但未拖入目标场景,刷卡时将无法通过。", this);
        }
    }

    private static bool IsInBuildSettings(string path)
    {
        foreach (var s in EditorBuildSettings.scenes)
            if (s.path == path) return true;
        return false;
    }

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

        // 出口标注目的地(LinearNext 不标;Scene / Ending 标出目标,编辑期一眼可查关卡图)
        string dest = "";
        if (type == AnchorType.Exit && destinationKind == DestinationKind.Scene)
            dest = $" → {System.IO.Path.GetFileNameWithoutExtension(destinationScenePath)}";
        else if (type == AnchorType.Exit && destinationKind == DestinationKind.Ending)
            dest = $" → 结局[{endingId}]";
        Handles.Label(transform.position + Vector3.up * 0.4f,
            $"{gameObject.name} · {(type == AnchorType.Entry ? "Entry" : "Exit")} · +Z 穿越方向{dest}");
    }
#endif
}
