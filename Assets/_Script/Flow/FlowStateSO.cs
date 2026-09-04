using UnityEngine;

/// <summary>
/// 游戏运行状态镜像(ScriptableObject,仿 PlayerStateSO 模式)。GameFlowManager 每次 SetState
/// 时写入当前 FlowState,各 UI / 内容组件轮询本资产快速判断"现在处于什么阶段"——
/// 不依赖流程实例是否存在(直玩时流程惰性,镜像仍可读),也不需要 FindObjectOfType。
///
/// 读取约定(与 PlayerStateSO 一致):
///  - 本资产是"运行状态快照",消费方按需轮询字段,不订阅事件;
///  - 运行期改动只在内存、不落盘(退出 Play 自动还原);
///  - Play 时在 Project 窗口选中本资产即可实时观察当前状态(调试用);
///  - 流程创建前的初始值是 Boot,UI 应把 Boot 视作"还没到任何界面"。
///
/// 生命周期:资产放 Assets/Resources/FlowState/ 下,静态 Instance 惰性加载。
/// UI 默认全部隐藏(场景里页面根 inactive);像主菜单这类"整块界面"由组件轮询
/// 本资产自驱显隐(进入 MainMenu 亮、离开隐藏),避免依赖流程逐处 Show/Hide。
/// </summary>
[CreateAssetMenu(fileName = "FlowStateSO", menuName = "Flow/Flow State SO")]
public class FlowStateSO : ScriptableObject
{
    [SerializeField, Tooltip("当前流程状态(由 GameFlowManager.SetState 写入)。")]
    private FlowState state = FlowState.Boot;

    // === 只读访问 ===
    public FlowState State => state;

    // === 派生快捷判断(UI 门控用) ===
    /// <summary>主菜单阶段(Room_00 背景 + 主菜单 UI 亮)。</summary>
    public bool InMainMenu => state == FlowState.MainMenu;
    /// <summary>游玩中(玩家场景 + 关卡已加载)。</summary>
    public bool InGame => state == FlowState.InGame;
    /// <summary>结局演出阶段。</summary>
    public bool InEnding => state == FlowState.Ending;
    /// <summary>开发者直玩(直接 Play 了房间 / 玩家场景,流程惰性)。</summary>
    public bool IsDevDirectPlay => state == FlowState.DevDirectPlay;
    /// <summary>是否允许按 Esc 开暂停(游玩中;直玩保持旧行为可暂停)。</summary>
    public bool PauseAllowed => InGame || IsDevDirectPlay;

    /// <summary>流程写入入口(GameFlowManager.SetState 调用)。</summary>
    public void Write(FlowState next)
    {
        state = next;
    }

    // === 静态访问 ===
    private static FlowStateSO _instance;
    private static bool loadFailed;   // 防资产缺失时每帧重复报错

    /// <summary>全局运行状态镜像。惰性从 Resources 加载;资产缺失时返回 null 并只报错一次。</summary>
    public static FlowStateSO Instance
    {
        get
        {
            if (_instance == null && !loadFailed)
            {
                _instance = Resources.Load<FlowStateSO>("FlowState/FlowStateSO");
                if (_instance == null)
                {
                    loadFailed = true;
                    Debug.LogError("[FlowStateSO] 找不到资产 Resources/FlowState/FlowStateSO，运行状态镜像不可用（UI 自驱会失效）");
                }
            }
            return _instance;
        }
    }
}
