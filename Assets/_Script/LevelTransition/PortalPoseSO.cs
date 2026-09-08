using UnityEngine;

/// <summary>
/// 门面位姿总线（ScriptableObject）—— 跨场景的"数据通道"。
///
/// 是什么:门面画面要用的三组世界数据(本关门锚点 / 目标门锚点 / 玩家位姿)的暂存处。
/// 场景资产不能跨场景拖引用,所以两家互不引用对方场景物体,只认这一个资产文件。
///
/// 谁写:出口门锚点上的 PortalDoor —— 门面工作期(Activate 后)每帧 Publish。
/// 谁读:目标关场景门后相机上的 PortalViewSync —— 照公式摆相机:
///   位置 = 目标门 .TransformPoint(本关门 .InverseTransformPoint(玩家));
///   朝向 = 目标门.rotation * Inverse(本关门.rotation) * 玩家相机朝向。
///
/// 怎么用(作者):Project 窗口 Assets/Resources/PortalPose/ 下右键 Create → Portal/Portal Pose SO,
/// 命名 PortalPoseSO;把该资产分别拖进 PortalDoor 的 Pose Bus 槽与 PortalViewSync 的 Pose 槽
/// (都不拖也能跑:代码按固定路径从 Resources 惰性加载;拖上是"引用可见 + 场景强引用防卸载")。
///
/// 常见坑:没创建资产 → 控制台报一条错,只有门面画面不出现,传送照常。同一时刻只有一道门
/// 在过渡(LevelTransitionManager 单过渡状态机),所以单槽位即可。
/// </summary>
[CreateAssetMenu(fileName = "PortalPoseSO", menuName = "Portal/Portal Pose SO")]
public class PortalPoseSO : ScriptableObject
{
    // === 本关门（出口）锚点世界位姿 ===
    [SerializeField] private Vector3 srcDoorPosition;
    [SerializeField] private Quaternion srcDoorRotation;

    // === 目标门（入口）锚点世界位姿 ===
    [SerializeField] private Vector3 dstDoorPosition;
    [SerializeField] private Quaternion dstDoorRotation;

    // === 玩家（相机）世界位姿 ===
    [SerializeField] private Vector3 playerPosition;
    [SerializeField] private Quaternion playerRotation;

    // === 元数据 ===
    [SerializeField] private bool active;              // 门面工作期（Activate 后 = true）
    [SerializeField] private float lastSyncedTime;    // 最近一次写入（Time.time）

    // === 只读访问 ===
    public Vector3 SrcDoorPosition => srcDoorPosition;
    public Quaternion SrcDoorRotation => srcDoorRotation;
    public Vector3 DstDoorPosition => dstDoorPosition;
    public Quaternion DstDoorRotation => dstDoorRotation;
    public Vector3 PlayerPosition => playerPosition;
    public Quaternion PlayerRotation => playerRotation;
    public bool IsActive => active;
    /// <summary>数据新鲜度（自上次写入起的时间秒）。读取方用它判断是否仍应同步/渲染。</summary>
    public float Age => Time.time - lastSyncedTime;

    /// <summary>门面工作期结束（穿过结算后由 PortalDoor 调用）：读取方据此停渲染。</summary>
    public void Deactivate()
    {
        active = false;
    }

    /// <summary>每帧写入（唯一调用方：PortalDoor）。</summary>
    public void Publish(Vector3 srcPos, Quaternion srcRot, Vector3 dstPos, Quaternion dstRot,
        Vector3 playerPos, Quaternion playerRot)
    {
        srcDoorPosition = srcPos;
        srcDoorRotation = srcRot;
        dstDoorPosition = dstPos;
        dstDoorRotation = dstRot;
        playerPosition = playerPos;
        playerRotation = playerRot;
        active = true;
        lastSyncedTime = Time.time;
    }

    // === 静态访问 ===
    private static PortalPoseSO _instance;
    private static bool loadFailed;   // 防资产缺失时每帧重复报错

    /// <summary>全局门面位姿总线。惰性从 Resources 加载；资产缺失时返回 null 并只报错一次。</summary>
    public static PortalPoseSO Instance
    {
        get
        {
            if (_instance == null && !loadFailed)
            {
                _instance = Resources.Load<PortalPoseSO>("PortalPose/PortalPoseSO");
                if (_instance == null)
                {
                    loadFailed = true;
                    Debug.LogError("[PortalPoseSO] 找不到资产 Resources/PortalPose/PortalPoseSO，门面位姿总线不可用（请创建该 SO 资产）");
                }
            }
            return _instance;
        }
    }
}
