using UnityEngine;

/// <summary>
/// 柴油机启动状态的跨场景镜像（ScriptableObject，仿 PlayerStateSO / FlowStateSO 模式）。
/// 写方 = Room_11 的 DieselGenerator（启动瞬间写一次 true，单向）；读方 = 后续关卡里
/// 需要知道"柴油机是否已启动"的系统，轮询字段即可 —— 不需要 FindObjectOfType 找柴油机，
/// 也不依赖 Room_11 是否已加载。
///
/// 取用方式（两种都可以，按场景选）：
///  - 拖引用：消费方各自持有 [SerializeField] 引用同一份资产，放在场景里、生命周期可见；
///  - 静态访问：GeneratorStateSO.Instance 惰性从 Resources 加载，适合不方便拖引用处。
/// 注意 Instance 在资产被 Resources.UnloadUnusedAssets() 卸载后会重新加载出**一份新实例**、
/// 状态归零 —— 关卡切换会调它（见 LevelTransitionManager）。需要跨关卡稳定的消费方
/// 请拖引用（引擎级强引用可防卸载），或在 Room_11 存活期间读取。
///
/// 读取约定（与 PlayerStateSO / FlowStateSO / LampPatternStateSO 一致）：
///  - 本资产是"运行状态快照"，消费方按需轮询字段，不订阅事件；
///  - 运行期改动只在内存、不落盘（退出 Play 自动还原资产，磁盘默认恒为 false）；
///  - Play 时在 Project 窗口选中本资产即可实时观察当前状态（调试用）。
///
/// 与存档的关系：需要跨"存档 / 读档"保留的**不是**本资产 —— 那由柴油机自己的
/// ISceneSaveable 承担；读档后柴油机按存档重写本镜像一次（恢复先于任何游玩帧），
/// 与 Room_02 / Room_03 八灯谜题同一套路。
/// </summary>
[CreateAssetMenu(fileName = "GeneratorStateSO", menuName = "Generator/Generator State SO")]
public class GeneratorStateSO : ScriptableObject
{
    [SerializeField, Tooltip("柴油机是否已启动（单向镜像布尔，非存档）。")]
    private bool started;

    /// <summary>柴油机当前是否已启动。</summary>
    public bool Started => started;

    /// <summary>写镜像（唯一写方：DieselGenerator）。</summary>
    public void PushStarted(bool value) => started = value;

    // === 静态访问 ===
    private static GeneratorStateSO _instance;
    private static bool loadFailed;   // 防资产缺失时每帧重复报错

    /// <summary>全局柴油机状态镜像。惰性从 Resources 加载；资产缺失时返回 null 并只报错一次。</summary>
    public static GeneratorStateSO Instance
    {
        get
        {
            if (_instance == null && !loadFailed)
            {
                _instance = Resources.Load<GeneratorStateSO>("Generator/GeneratorStateSO");
                if (_instance == null)
                {
                    loadFailed = true;
                    Debug.LogError("[GeneratorStateSO] 找不到资产 Resources/Generator/GeneratorStateSO，柴油机状态镜像不可用");
                }
            }
            return _instance;
        }
    }
}
