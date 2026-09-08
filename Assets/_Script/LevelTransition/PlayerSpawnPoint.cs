using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// 玩家出生点标记：标出「本关开局 / 直达换场景落点把玩家放到哪」。挂在关卡场景的出生位置。
///
/// 与门 / 锚点系统的分工：
///   - 入口锚点（LevelAnchor Entry）仍是「穿过门后的落点」，归门演出路径（PortalDoor）；
///   - 本标记服务「无门转换」的落点 —— LevelTransitionManager 开局（BeginRun）与直达
///     （RequestDirectSwitch 带落点）加载完成时把玩家传送到目标关的默认出生点；
///   - 读档的落点来自存档里的玩家快照，与本标记无关。
///
/// 作者约定：
///   - 每关摆一个默认出生点；多摆了取根序遍历第一个（与 LevelAnchor 同序，确定性）。
///     将来要「多个可点名落点」（剧情分支直达不同位置）时，再加 spawnId 字符串字段与
///     按名查找 —— 默认空串 = 默认出生点，现有场景零迁移；
///   - 位置 = 蜜蜂刚体中心要停的点（Y 一般 = 地板 + 碰撞体半径）；仅 +Z 的水平朝向有效
///     = 出生正视方向（俯仰写 0 即可，代码落点只取水平角）；localScale 无关。
///
/// 场景没摆本标记时开局 / 直达保持旧行为（玩家原位），只留日志 —— 老关零改动可跑。
/// </summary>
public class PlayerSpawnPoint : MonoBehaviour
{
    /// <summary>指定场景的默认出生点（场景根序遍历的第一个；无标记 = null）。</summary>
    public static PlayerSpawnPoint FindDefault(Scene scene)
    {
        foreach (var spawn in FindAll(scene))
            return spawn;
        return null;
    }

    /// <summary>指定场景的全部出生点（编辑 / 校验用）。</summary>
    public static List<PlayerSpawnPoint> FindAll(Scene scene)
    {
        var result = new List<PlayerSpawnPoint>();
        if (!scene.IsValid() || !scene.isLoaded) return result;
        foreach (var root in scene.GetRootGameObjects())
            result.AddRange(root.GetComponentsInChildren<PlayerSpawnPoint>(true));
        return result;
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        // 出生正视方向箭头(+Z, 与锚点同色系区分:出生点用青绿) + 落点参考圈(半径 0.5 ≈ 玩家球碰撞体,
        // 一眼核对贴地高度 —— 参考圈应恰好搁在想停的平面上,别沉进地板也别悬空)
        Gizmos.color = new Color(0.2f, 0.9f, 0.8f, 0.9f);
        Vector3 tip = transform.position + transform.forward * 1.2f;
        Gizmos.DrawLine(transform.position, tip);
        Gizmos.DrawSphere(tip, 0.08f);
        Gizmos.DrawWireSphere(transform.position, 0.5f);
        Handles.Label(transform.position + Vector3.up * 0.5f, $"{gameObject.name} · Spawn · +Z 出生朝向");
    }
#endif
}
