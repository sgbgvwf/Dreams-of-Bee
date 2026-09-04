using UnityEngine;

/// <summary>
/// 玩家跨场景状态镜像（ScriptableObject）。玩家数据（位置/旋转/缩放/速度/运动状态/体力/视线/持物）
/// 每帧由挂在玩家身上的 PlayerStateSync 写入，其它场景 / 脚本通过静态 PlayerStateSO.Instance 读取 ——
/// 不需要 FindObjectOfType 找玩家物体，也不依赖玩家场景是否已加载。
///
/// 读取约定：
///  - 本资产是"镜像快照"，消费方按需轮询字段，不订阅事件；
///  - 运行期改动只在内存、不落盘（退出 Play 自动还原资产，这是 SO 镜像的标准行为）；
///  - Play 时在 Project 窗口选中本资产即可实时观察玩家的当前数值（调试用）；
///  - 玩家同步前字段是缺省值（maxStamina=0 等），可配合 lastSyncedTime 判断数据是否已就绪。
///
/// 生命周期：资产放 Assets/Resources/ 下，静态 Instance 惰性加载；Player.unity 的 PlayerStateSync
/// 持有序列化引用 —— 引擎级强引用保证 LevelTransitionManager 卸载上一关时调用的
/// Resources.UnloadUnusedAssets() 不会卸载本资产（仅靠静态字段引用的资产可能被卸载）。
/// </summary>
[CreateAssetMenu(fileName = "PlayerStateSO", menuName = "Player/Player State SO")]
public class PlayerStateSO : ScriptableObject
{
    // === 位姿（由 PlayerStateSync 每帧从玩家 Transform 镜像） ===
    [SerializeField, Tooltip("玩家世界坐标（渲染帧末快照，含物理插值）。")]
    private Vector3 position;
    [SerializeField, Tooltip("玩家世界旋转。")]
    private Quaternion rotation;
    [SerializeField, Tooltip("玩家 localScale（当前玩法恒为 1，镜像以备未来缩放机制）。")]
    private Vector3 scale;

    // === 运动 ===
    [SerializeField, Tooltip("刚体速度（最近一次物理步）。")]
    private Vector3 velocity;
    [SerializeField, Tooltip("运动状态：Crawling=0 爬行 / Flying=1 飞行 / Falling=2 坠落。")]
    private BeeFlightController.BeeState movementState;

    // === 体力 ===
    [SerializeField, Tooltip("当前体力（飞行消耗、其余状态恢复）。")]
    private float stamina;
    [SerializeField, Tooltip("体力上限。")]
    private float maxStamina;

    // === 视线 ===
    [SerializeField, Tooltip("世界空间视线旋转（yaw→pitch，与 BeeFlightController.CameraRotation 一致）。")]
    private Quaternion lookRotation;

    // === 持物 ===
    [SerializeField, Tooltip("是否正在携带物品。")]
    private bool isHolding;
    [SerializeField, Tooltip("所持物品的 Id（Interactable.itemId；空串 = 物品未配置身份）。")]
    private string heldItemId = "";

    // === 元数据 ===
    [SerializeField, Tooltip("最近一次同步的时间（Time.time）。消费方可用它判断快照是否新鲜。")]
    private float lastSyncedTime;

    // === 只读访问 ===
    public Vector3 Position => position;
    public Quaternion Rotation => rotation;
    public Vector3 Scale => scale;
    public Vector3 Velocity => velocity;
    public BeeFlightController.BeeState MovementState => movementState;
    public float Stamina => stamina;
    public float MaxStamina => maxStamina;
    /// <summary>归一化体力（0~1），HUD 等直接可用。</summary>
    public float StaminaNormalized => maxStamina > 0f ? stamina / maxStamina : 0f;
    public Quaternion LookRotation => lookRotation;
    public bool IsHolding => isHolding;
    public string HeldItemId => heldItemId;
    public float LastSyncedTime => lastSyncedTime;

    // === 写入（唯一调用方：PlayerStateSync） ===
    public void SyncSnapshot(Vector3 pos, Quaternion rot, Vector3 localScale, Vector3 vel,
        BeeFlightController.BeeState state, float current, float max, Quaternion look,
        bool holding, string heldId)
    {
        position = pos;
        rotation = rot;
        scale = localScale;
        velocity = vel;
        movementState = state;
        stamina = current;
        maxStamina = max;
        lookRotation = look;
        isHolding = holding;
        heldItemId = heldId ?? "";
        lastSyncedTime = Time.time;
    }

    // === 静态访问 ===
    private static PlayerStateSO _instance;
    private static bool loadFailed;   // 防资产缺失时每帧重复报错

    /// <summary>全局玩家状态镜像。惰性从 Resources 加载；资产缺失时返回 null 并只报错一次。</summary>
    public static PlayerStateSO Instance
    {
        get
        {
            if (_instance == null && !loadFailed)
            {
                _instance = Resources.Load<PlayerStateSO>("PlayerState/PlayerStateSO");
                if (_instance == null)
                {
                    loadFailed = true;
                    Debug.LogError("[PlayerStateSO] 找不到资产 Resources/PlayerState/PlayerStateSO，玩家状态镜像不可用");
                }
            }
            return _instance;
        }
    }
}
