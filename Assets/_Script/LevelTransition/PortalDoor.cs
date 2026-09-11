using UnityEngine;

/// <summary>
/// 传送门 —— 出口门洞上的「穿门 + 门面供数」组件。
///
/// 是什么:一扇单向传送门。玩家刷卡后门打开,玩家碰到门面触发区即被传送到目标关的对应门洞
/// (位姿/速度/朝向按两门相对变换换算,画面连续);同时在工作期把门面画面要用的数据
/// (本关门锚点 / 目标门锚点 / 玩家位姿)每帧写进 PortalPoseSO 总线。
///
/// 放哪:挂在出口锚点(LevelAnchor Exit)所在物体上 —— 即"玩家看过去、刷卡穿过去的那扇门"。
/// 目标关(入口侧)不挂任何东西,只有锚点做位姿参照(见 LevelAnchor 头注释)。
///
/// 穿门判定谁负责:门面平面(贴 RenderTexture 的那张 Quad)上的 PortalCrossTrigger ——
/// 它接住玩家碰撞体后调本组件的 OnFaceTouched。本组件不做任何"玩家在门平面哪一侧"的推算:
/// 触发体在哪、多大,判定就在哪、多大(要提前/延后传送 = 调那个触发体,别改这里)。
///
/// 绑什么:字段只有一个 ——
///   - Pose Bus:可拖 PortalPoseSO 资产(不拖则自动从 Resources 加载;拖上引用可见可查、防资产被卸载)。
///
/// 门面画面怎么来(与画面资产的分工):本组件【不创建、不管理任何画面资源】。
/// 画面 = 作者自摆:本关门洞处一张平面贴 RenderTexture(显示端);目标关场景一台相机
/// (挂 PortalViewSync,Target Texture 指向同一张 RT,渲染目标关画面)。本组件只把数据写进
/// PortalPoseSO,PortalViewSync 读总线、按同一相对公式摆相机 —— 用法见 PortalViewSync.cs 头注释。
///
/// 常见坑:
///   - 本关门与目标门两锚点 +Z 必须沿同一条连续前进路径成对摆放(见 LevelAnchor 头注释),
///     转反一侧 = 门面平面背对玩家不可见 / 穿门落点朝向反;
///   - 没建 PortalPoseSO 资产 → 控制台报一条错,只有门面画面不出现,传送照常;
///   - 玩家碰到门面却不传送 → 查 Quad 上的 PortalCrossTrigger 是否拖了本组件、触发体是否 Is Trigger;
///   - 只有玩家本体(身上带 Rigidbody 的那个物体)能触发:手里举着的钥匙/道具自带 Rigidbody,
///     碰到门面不会把人提前传走;
///   - 未 Activate(门还没开)时本组件一律不理,此时玩家撞到的是门板,碰不到门面触发区。
/// </summary>
public class PortalDoor : MonoBehaviour
{
    [Header("门面位姿总线")]
    [SerializeField, Tooltip("可拖的 PortalPoseSO 资产(数据通道:本组件写、目标关相机上的 PortalViewSync 读)。空 = 运行时从 Resources/PortalPose/PortalPoseSO 惰性加载。拖上 = Inspector 可见可查、场景持有强引用防卸载关卡时被资源清理。")]
    private PortalPoseSO poseBus;

    // --- 运行时解析 ---
    private SlidingDoor exitDoor;       // 本关出口门
    private Transform player;           // 玩家(tag "Player",Player 常驻场景)
    private Rigidbody playerBody;       // 玩家本体刚体(门面触发区只认它,见 OnFaceTouched)
    private BeeFlightController flight; // 玩家身上的飞行控制器(传送 + 视角读取)
    private Transform entryAnchor;      // 目标关入口锚点(Activate 时由管理器注入)

    // --- 状态 ---
    private bool active;                // Activate(门面工作期开始)后为 true
    private bool crossed;               // 已穿过:一次性(单向)
    private bool setupDone;

    /// <summary>由 LevelTransitionManager 在 T1 激活(目标关已加载、出口门即将打开)。</summary>
    public void Activate(Transform entryAnchor)
    {
        this.entryAnchor = entryAnchor;
        if (!EnsureSetup()) return;

        active = true;
        Debug.Log($"[PortalDoor] 传送门已激活：{name} → {entryAnchor.name}", this);
    }

    private void Update()
    {
        if (!active || crossed) return;
        if (player == null && !ResolvePlayer()) return;

        // 门面工作期:每帧把门面画面要用的数据写进总线(目标关相机上的 PortalViewSync 读它摆位姿)
        PublishPoseToBus();
    }

    /// <summary>把门面位姿三组数据写入 PortalPoseSO(本组件是总线的唯一写入方)。
    /// 读取方:目标关相机上的 PortalViewSync,照同一公式摆自己。</summary>
    private void PublishPoseToBus()
    {
        var bus = poseBus != null ? poseBus : PortalPoseSO.Instance;   // 拖的优先,空则惰性加载
        if (bus == null || entryAnchor == null) return;
        bus.Publish(
            transform.position, transform.rotation,          // 本关门(出口)锚点
            entryAnchor.position, entryAnchor.rotation,      // 目标门(入口)锚点
            player.position,                                 // 玩家相机位置(相机每帧硬同步到身体)
            flight != null ? flight.CameraRotation : player.rotation);   // 玩家视线
    }

    // ==================== 初始化与门平面基准 ====================

    private bool EnsureSetup()
    {
        if (setupDone) return true;
        setupDone = true;

        ResolvePlayer();
        // 出口门(门板 BoxCollider)仍是配置必需项:它框出门洞矩形,编辑期 Gizmos 靠它画出
        // 门面平面 / 触发体的摆放参考(见 OnDrawGizmos)。
        if (!TryGetDoorBox(out _) || entryAnchor == null)
        {
            Debug.LogError($"[PortalDoor] 传送门无法初始化：需要出口锚点(LevelAnchor)、出口门(门板 BoxCollider)与入口锚点。{name}", this);
            return false;
        }

        return true;
    }

    /// <summary>门平面位置:门洞中心沿穿越方向反向退回门板深度的一半(贴房间内侧墙面)。
    /// 编辑期 Gizmos 用它标出门洞矩形(见 OnDrawGizmos);运行时穿门判定不再依赖它
    /// ——触发体摆在门面 Quad 上(见 PortalCrossTrigger)。</summary>
    private Vector3 ComputePlanePos(BoxCollider box) =>
        transform.position - transform.rotation * Vector3.forward * (box.size.z * 0.5f);

    /// <summary>解析出口门及其门板碰撞体(运行时与 Gizmos 共用)。</summary>
    private bool TryGetDoorBox(out BoxCollider box)
    {
        box = null;
        var anchor = GetComponent<LevelAnchor>();
        exitDoor = anchor != null ? anchor.ResolveExitDoor() : null;
        if (exitDoor != null)
            box = exitDoor.DoorPanelCollider as BoxCollider;
        return box != null;
    }

    private bool ResolvePlayer()
    {
        var go = GameObject.FindGameObjectWithTag("Player");
        if (go == null) return false;
        player = go.transform;
        flight = go.GetComponent<BeeFlightController>();
        playerBody = go.GetComponent<Rigidbody>();   // 门面触发区只认这个刚体(见 OnFaceTouched)
        return flight != null;
    }

    // ==================== 穿过判定与传送 ====================

    /// <summary>
    /// 门面触发区(门面 Quad 上的 PortalCrossTrigger)接住玩家时调用 —— 立即传送。
    /// 只认玩家本体:手里举着的钥匙/道具自带 Rigidbody,碰到门面不会把人提前传走。
    /// 触发体摆在哪、多大由作者决定(见 PortalCrossTrigger 头注释);本组件不推算玩家在门哪一侧 ——
    /// 要提前/延后传送就改那个触发体,别改这里。
    /// </summary>
    public void OnFaceTouched(Collider other)
    {
        if (!active || crossed) return;                      // 端口未激活(门还没开)/ 已穿过:一律不理
        if (player == null && !ResolvePlayer()) return;
        if (playerBody == null || other == null) return;
        if (other.attachedRigidbody != playerBody) return;   // 不是玩家本体(例如手里举着的钥匙)

        Cross();
    }

    private void Cross()
    {
        crossed = true;

        // 门面工作期结束:停总线(目标关相机上的 PortalViewSync 检测到数据停更后自动停渲染)
        if (poseBus != null) poseBus.Deactivate();
        else PortalPoseSO.Instance?.Deactivate();

        flight.ApplyPortalTransform(transform, entryAnchor);   // 传送(速度 / 朝向同步换算)

        if (LevelTransitionManager.Instance != null)
            LevelTransitionManager.Instance.OnPortalCrossed(this);
        else
            Debug.LogError("[PortalDoor] 找不到 LevelTransitionManager，传送后无法卸载前场景", this);
    }

    // ==================== 编辑期辅助:Gizmos ====================

    private void OnDrawGizmos()
    {
        // 门洞矩形 + 穿越方向(编辑期核对门洞尺寸与朝向;作者摆门面平面 / 触发体时以此矩形为准)
        if (!TryGetDoorBox(out var box)) return;

        Vector3 pos = ComputePlanePos(box);
        Quaternion rot = transform.rotation;
        Vector3 size = new Vector3(box.size.x, box.size.y, 0.01f);

        Gizmos.matrix = Matrix4x4.TRS(pos, rot, size);
        Gizmos.color = new Color(0.2f, 0.7f, 1f, 0.30f);
        Gizmos.DrawCube(Vector3.zero, Vector3.one);          // 门洞矩形(半透明;门面平面 / 触发体摆放参考)
        Gizmos.color = new Color(0.2f, 0.7f, 1f, 0.9f);
        Gizmos.DrawWireCube(Vector3.zero, Vector3.one);      // 门洞矩形边框
        Gizmos.matrix = Matrix4x4.identity;

        // 穿越方向(+Z)
        Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.9f);
        Vector3 tip = pos + rot * Vector3.forward * 1.2f;
        Gizmos.DrawLine(pos, tip);
        Gizmos.DrawSphere(tip, 0.07f);

#if UNITY_EDITOR
        // 约定检查(仅编辑期):门板关闭位应与锚点重合 —— 门平面以锚点为基准
        if (!Application.isPlaying && exitDoor != null && exitDoor.DoorPanel != null)
        {
            float d = Vector3.Distance(transform.position, exitDoor.DoorPanel.position);
            if (d > 0.2f)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawLine(transform.position, exitDoor.DoorPanel.position);
                Gizmos.DrawWireSphere(exitDoor.DoorPanel.position, 0.3f);
            }
        }
#endif
    }
}
