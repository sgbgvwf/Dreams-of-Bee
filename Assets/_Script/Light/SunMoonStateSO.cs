using UnityEngine;

/// <summary>
/// 昼夜天体亮度的跨场景镜像（ScriptableObject，仿 FlowStateSO 模式）。
/// 写方 = SunMoonCycle（每帧写入太阳 / 月亮的归一化亮度）；读方 = 任何需要知道
/// “现在太阳多亮、月亮多亮”的系统（雾效、后处理、天空、UI 等），轮询字段即可 ——
/// 不需要 FindObjectOfType 找 SunMoonCycle，也不依赖 Room_04 是否已加载。
///
/// 数值含义：0 = 完全熄灭（天体不可见），1 = 全亮（场景里配的满亮度基线）。
/// 两者此消彼长，任一时刻 SunBrightness + MoonBrightness = 1。
///
/// 读取约定（与 PlayerStateSO / FlowStateSO 一致）：
///  - 本资产是“运行状态快照”，消费方按需轮询字段，不订阅事件；
///  - 运行期改动只在内存、不落盘（退出 Play 自动还原资产）；
///  - Play 时在 Project 窗口选中本资产即可实时观察当前亮度（调试用）。
///
/// 生命周期：资产放 Assets/Resources/SunMoon/ 下，静态 Instance 惰性加载；
/// SunMoonCycle 持有序列化引用 —— 引擎级强引用保证 LevelTransitionManager 切换关卡时
/// 调用的 Resources.UnloadUnusedAssets() 不会卸载本资产（仅靠静态字段引用的资产可能被卸载）。
/// </summary>
[CreateAssetMenu(fileName = "SunMoonStateSO", menuName = "Light/Sun Moon State SO")]
public class SunMoonStateSO : ScriptableObject
{
    [SerializeField, Range(0f, 1f), Tooltip("太阳当前亮度百分比（0 = 全灭，1 = 全亮）。")]
    private float sunBrightness;

    [SerializeField, Range(0f, 1f), Tooltip("月亮当前亮度百分比（0 = 全灭，1 = 全亮）。")]
    private float moonBrightness;

    // === 只读访问 ===
    /// <summary>太阳当前亮度百分比（0..1）。</summary>
    public float SunBrightness => sunBrightness;

    /// <summary>月亮当前亮度百分比（0..1）。</summary>
    public float MoonBrightness => moonBrightness;

    /// <summary>写镜像（唯一写方：SunMoonCycle）。</summary>
    public void Push(float sun, float moon)
    {
        sunBrightness = Mathf.Clamp01(sun);
        moonBrightness = Mathf.Clamp01(moon);
    }

    // === 静态访问 ===
    private static SunMoonStateSO _instance;
    private static bool loadFailed;   // 防资产缺失时每帧重复报错

    /// <summary>全局天体亮度镜像。惰性从 Resources 加载；资产缺失时返回 null 并只报错一次。</summary>
    public static SunMoonStateSO Instance
    {
        get
        {
            if (_instance == null && !loadFailed)
            {
                _instance = Resources.Load<SunMoonStateSO>("SunMoon/SunMoonStateSO");
                if (_instance == null)
                {
                    loadFailed = true;
                    Debug.LogError("[SunMoonStateSO] 找不到资产 Resources/SunMoon/SunMoonStateSO，天体亮度镜像不可用");
                }
            }
            return _instance;
        }
    }
}
