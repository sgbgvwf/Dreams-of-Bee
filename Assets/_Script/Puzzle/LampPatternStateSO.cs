using UnityEngine;

/// <summary>
/// 八灯图案谜题的跨场景达成镜像（ScriptableObject）。
/// 写方 = Room_02 的 LampPatternWatcher（图案状态翻转时才写）；读方 = Room_03 的 FogReward（每帧轮询）。
/// 双方各自持有 [SerializeField] 引用同一份资产即可，无需单例 / Resources.Load。
/// 运行期改动只在内存不落盘（磁盘默认恒为 false）。
/// </summary>
[CreateAssetMenu(fileName = "LampPatternStateSO", menuName = "Lamp Puzzle/Lamp Pattern State SO")]
public class LampPatternStateSO : ScriptableObject
{
    [SerializeField, Tooltip("八灯自定义图案是否已达成（镜像布尔，非存档）。")]
    private bool solved;

    /// <summary>图案当前是否达成。</summary>
    public bool Solved => solved;

    /// <summary>写镜像（唯一写方：LampPatternWatcher）。</summary>
    public void PushSolved(bool value) => solved = value;
}
