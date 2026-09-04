using UnityEngine;

/// <summary>
/// 场景物件存档接口(存档核心对类型封闭,扩展靠实现本接口):
/// SaveSystem 采集时扫描当前关卡场景里所有实现本接口的组件,恢复时按
/// TransformPath 找到组件并把存档 JSON 还给它 —— 存档核心不感知任何具体类型,
/// 新增一种可存档物件 = 在该组件上实现本接口 + 它自己的 DTO,零核心改动。
///
/// 最小样板(以"可动平台 MovingPlatform"为例):
///   [Serializable] public sealed class PlatformState { public bool extended; }   // 放 SaveData.cs 或组件文件内
///   public class MovingPlatform : MonoBehaviour, ISceneSaveable
///   {
///       public string SaveableType => "MovingPlatform";        // 与存档条目 type 配对(防组件被换后错配)
///       public string CaptureToJson() => JsonUtility.ToJson(new PlatformState { extended = IsExtended });
///       public void RestoreFromJson(string json)
///       {
///           var s = JsonUtility.FromJson<PlatformState>(json);
///           SetExtended(s.extended);                            // 组件自己的幂等状态入口
///       }
///   }
/// 身份 = 组件所在物体的场景内 transform 路径(TransformPath),组件无需配置任何 id。
/// 注意:恢复发生在场景刚实例化、任何游玩帧之前 —— 基线(Awake 捕获等)都是场景默认态,
/// 恢复必须走组件"幂等设置目标态"的入口,不要依赖增量动画。
/// </summary>
public interface ISceneSaveable
{
    /// <summary>组件类型标识(存档条目 type 的记录与恢复校验;实现方返回自己的常量字符串)。</summary>
    string SaveableType { get; }

    /// <summary>把组件当前状态打包成它自己的 DTO JSON(JsonUtility.ToJson)。</summary>
    string CaptureToJson();

    /// <summary>按存档 JSON 恢复组件状态(组件自身 DTO;json 损坏时 JsonUtility 会给默认值,不要抛异常)。</summary>
    void RestoreFromJson(string json);
}
