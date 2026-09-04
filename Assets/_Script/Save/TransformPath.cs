using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 场景内物体身份的"名字路径"(存档的身份方案):存档不依赖逐物配置 id,
/// 用 场景根 → 目标物体 的完整路径(如 "ExitDoorAnchor/传送门/PortalPlane") 当稳定键。
///
/// 作者约定:同一场景内物体名保持唯一(Unity 允许重名,存档要求避讳)。
/// 路径按名字匹配(不含索引),因此中间节点的改名 / 挪层级会让旧存档指向错误或落空——
/// 落空时恢复侧会警告并跳过该条,不会崩溃。
/// 中文物体名(门禁卡 / 移动门…)是纯字符串,无编码问题。
/// </summary>
public static class TransformPath
{
    /// <summary>生成 target → 场景根的完整名字路径(含根名;target 自身是根时返回根名)。</summary>
    public static string GetPath(Transform target)
    {
        if (target == null) return "";
        var sb = new StringBuilder(target.name);
        Transform t = target.parent;
        while (t != null)
        {
            sb.Insert(0, t.name + "/");
            t = t.parent;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 在指定场景中按名字路径找物体(逐节点名字比对,顶层必须是该场景的根)。
    /// 找不到 / 场景未加载 → 返回 null 并警告一次。
    /// </summary>
    public static Transform FindByPath(Scene scene, string path)
    {
        if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(path)) return null;

        var roots = scene.GetRootGameObjects();
        foreach (var root in roots)
        {
            var rootName = root.name + "/";
            if (!path.StartsWith(rootName, System.StringComparison.Ordinal) && path != root.name)
                continue;   // 顶层不匹配 → 不在这个根下,整棵跳过(比逐节点比对快)

            var candidates = root.GetComponentsInChildren<Transform>(true);
            foreach (var c in candidates)
            {
                if (string.Equals(GetPath(c), path, System.StringComparison.Ordinal))
                    return c;
            }
            return null;   // 顶层对上了但没找到完整链 → 物体已改名 / 被删,不必再找别的根
        }
        return null;
    }
}
