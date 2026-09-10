using System.Collections;
using UnityEngine;

/// <summary>
/// 落水判定与回送（关卡侧脚本）：挂在有水域的关卡物体上（Room_03 的 Ocean 等），
/// 玩家蜜蜂球心低过水位线即判定落水 → 全屏渐黑 → 传送回"落水前最后趴着的位置" → 渐出。
///
/// 数据通道（按项目约定，读写分工）:
///   - 读取 = PlayerStateSO 镜像（位置 / 运动状态 / 视线 / 就绪标记）:PlayerStateSync 每渲染帧末
///     写入，本组件在 Update 零耦合轮询 —— 不 Find 玩家物体、不怕玩家场景加载晚于关卡；
///     判定与"玩家画面上看见的落水瞬间"天然一致（镜像即当前画面上的玩家）。
///     镜像未就绪（lastSyncedTime 缺省 = 玩家场景未加载，如开发者直玩房间场景）→ 本组件空转，不误判。
///   - 动作 = 回送瞬间才按 tag "Player" 惰性解析 BeeFlightController（仿 PortalDoor：
///     关卡侧组件要"动玩家"时统一这么拿），顺带锁 InputLocked 防黑幕期间盲飞。
///
/// 安全点（回送位置）记录规则 —— 防"送回半空又落水"死循环的根本保证:
///   - 只在玩家有支撑时记（MovementState == Crawling,即趴着；引擎里趴墙 = 站甲板,同一状态），
///     飞行 / 坠落中的空中位置一律不记;
///   - 每次从空中落到表面的瞬间立即记一次（爬墙告一段落 = 立刻有新鲜锚点,不等 5 秒）;
///   - 趴着期间每 recordIntervalSeconds 秒刷新一次;
///   - 低过水位线的位置不记（船壳水下段等不产生锚点 → 锚点必然在水面以上,
///     回送后不会立刻又判定落水）。
///   锚点同时存当时的视线水平角（从镜像 LookRotation 分解）:回送时还原朝向
///   （TeleportTo 的 yaw,俯仰归零）。
///
/// 判定时机:镜像位置下穿水位线的瞬间触发;一直泡在水下不会重复触发（wasUnderwater 边沿锁,
/// 回送失败保持锁 → 只报一次错,不黑屏死循环）。
///
/// 救援目标选择（无锚点时不报错,按序兜底）:
///   1. 正常路径 = 回送锚点(落水前最后趴着的位置,见上);
///   2. 从未趴过 / 没记到锚点（如开局即悬空直接落水）→ 以落水点为球心向上半球球扫,
///      只认"朝上的面"（甲板顶 / 平台顶 / 地面;侧壁与平台下沿一律不算,理由见下）,
///      取最近一处的面顶贴着放下;落地瞬间 Update 会自动重记锚点,不会有第二段无锚点期;
///   3. 附近实在没有（掉进开阔海）→ 回送"进入本关时的位姿"（记录规则见下）—— 本关唯一已知
///      安全的落点,玩家由此回到他进本关时所在的位置;
///   4. 连进入位姿都没记到（玩家从未在本关到位过 / 关卡一直没进稳定点）→ 配置错误,
///      LogError 一次不回送（与"回送失败保持锁"同理,不黑屏死循环）。
///
/// 兜底面为什么只认朝上的面:蜜蜂虽然任意面都能趴（见 BeeFlightController）,但兜底救援只是
/// 把球心放到"命中点 + 法线 × 玩家半径"处、不施加任何侧向力 —— 面朝上时球心落在面顶,
/// 自由落下 5cm 即接触趴住;放的是竖直侧壁（法线水平,球心悬在面旁）/ 平台下沿（法线朝下,
/// 球心悬在面下）时,蜜蜂接触不到任何东西,一路坠落再落水,救援反复空转 —— 玩家永远回不到
/// 岸上,正是"救援生效了反而无法自救"的那条路径。
///
/// 进入本关时的位姿记录规则（兜底链最后一环）:本关进入稳定点(LevelTransitionManager.IsSettled)
/// 后第一个"镜像新鲜 + 球心高于水位线"的帧记一次,此后不再更新 —— 那一刻玩家已被放到出生点 /
/// 从门里走出 / 读档恢复完,正是"进入场景时的情况";开发者单开房间场景(无 LevelTransitionManager)
/// 视为已就绪,首次拿到新鲜镜像即记(那时玩家就在玩家场景编好的初始位)。
/// 兜底位姿（朝上面命中点 / 进入本关位姿）一律保证球心高于水位线,不会回送后立刻又判落水。
///
/// 救援演出:黑幕用常驻的 FadeOverlay(开发者直玩无 Persistance 时其静态方法空转 → 无黑幕瞬移)。
///
/// 用法:在关卡里给水域物体(Ocean)加本组件,把 waterLevelY 填成"水面世界 Y"。
/// 水位线是显式数值、不自动读场景 —— 以后挪了 Ocean 记得同步改 waterLevelY。
/// 抄的是世界 Y,别抄局部值:Room_03 的 Ocean 挂在 y = -100 的父物体下、自身局部 y = 3.34,
/// 世界水面 ≈ -96.66;场景里现填 -96(比水面低 0.66m,即"沉到水面下一点才判落水")。
/// 若锚点所在表面之后被移走(如滑门开门),回送后蜜蜂会从原处坠落 ——
/// 但锚点每 5 秒 / 每次落地都在刷新,下一站很快被记录,不会卡死在坏点上。
/// </summary>
public class DrownReturn : MonoBehaviour
{
    /// <summary>镜像快照允许的最大年龄(秒):超过视为玩家已卸载,停止判定(不误判缺省值 / 陈旧数据)。</summary>
    private const float MaxSnapshotAgeSeconds = 0.5f;

    [Header("落水判定")]
    [SerializeField, Tooltip("水位线(世界 Y)。玩家球心低于此高度即判定落水 —— Room_03 的 Ocean 世界水面 ≈ -96.66(局部 y 3.34 + 父物体 y -100)。")]
    private float waterLevelY = 0f;

    [Header("安全点记录")]
    [SerializeField, Tooltip("趴着期间刷新安全点的间隔(秒);另外每次从空中落到表面的瞬间会立即记一次。")]
    private float recordIntervalSeconds = 5f;

    [Header("救援节奏")]
    [SerializeField, Tooltip("落水后渐黑时长(秒);全黑停留时长由 FadeOverlay.holdBlackSeconds 全局统一。")]
    private float fadeInSeconds = 0.35f;
    [SerializeField, Tooltip("回送完成后渐出时长(秒)。")]
    private float fadeOutSeconds = 0.4f;

    [Header("无锚点兜底(从未趴过就落水)")]
    [SerializeField, Tooltip("兜底救援半径(米):以落水点为球心向上半球球扫(16 方位 × 5 仰角 + 正上方),\n只认朝上的面(甲板顶 / 平台顶 / 地面),取最近一处的面顶贴着放下。\n搜不到才送回进入本关时的位姿。")]
    private float rescueSearchRadius = 30f;

    [SerializeField, Range(0f, 1f), Tooltip("救援面\"朝上\"的门槛(命中法线的竖直分量 normal.y 下限):0.5 ≈ 坡度 60° 以内。\n低于门槛的侧壁 / 平台下沿一律不作救援点 —— 球心会悬在面旁 / 面下,蜜蜂接触不到、直接坠落,救援空转。")]
    private float minUpwardNormalY = 0.5f;

    // --- 兜底表面搜索常量 ---
    private const float BeeColliderRadius = 0.5f;    // 玩家 SphereCollider 半径(与出生点 Gizmo 参考圈一致)
    private const float SurfacePlaceGap = 0.05f;     // 贴面放置离缝(米):球心 = 命中点 + 法线 × (半径 + 离缝),悬停面顶上方一点点,落下即趴
    private const float MinClearanceYAboveWater = 0.15f;  // 放置后球心至少高出水位线这么多(防回送后立刻又判落水)
    private const float MinCastDistance = 0.05f;     // 小于此距离的命中视为"起点已在表面内 / 自身碰撞体",作废
    private const int AzimuthCount = 16;             // 四周球扫的方位数(船边的甲板可能只从某一两个方位探得到,方位太稀会整趟漏掉)
    private const int SurfaceHitBufferSize = 16;     // 单次球扫的命中缓存大小:固定数组、不产生 GC;一条射线在关卡里穿到的碰撞体远少于 16
    private static readonly float[] ElevationsDegrees = { 5f, 15f, 35f, 55f, 75f };  // 四周球扫的仰角(水平向上):5° 掠射够到贴水面的码头 / 甲板,向上逐级覆盖到高处的平台顶面

    // --- 位姿记录(位置 + 当时的视线水平角;俯仰不存 —— 回送按项目约定俯仰归零) ---
    private struct PoseYaw
    {
        public Vector3 position;
        public float yawDegrees;
    }

    private PoseYaw safePoint;   // 安全点:落水前最后趴着的位置
    private bool hasSafePoint;   // 尚无任何趴点记录(落水但没锚点 → 走 RescueRoutine 的兜底链:就近朝上的面 → 进入本关位姿)
    private PoseYaw entryPose;   // 进入本关时的位姿:兜底链最后一环
    private bool hasEntryPose;   // 尚未观测到"玩家已在本关到位(稳定点)+ 水面以上"的镜像帧

    // --- 边沿与节流 ---
    private BeeFlightController.BeeState prevState = BeeFlightController.BeeState.Flying;
    private float recordTimer;   // 趴着期间距上次记录的时间
    private bool wasUnderwater;  // 上一帧球心是否已在水中(下穿边沿 → 只触发一次)
    private bool rescuing;       // 救援协程进行中(防重入)
    private readonly RaycastHit[] surfaceHits = new RaycastHit[SurfaceHitBufferSize];   // 兜底球扫的命中缓存(一次救援复用,不逐次分配)

    private void Update()
    {
        if (PauseMenu.IsPaused) return;   // 暂停菜单打开时让出(同 BeeFlightController 的 Update 门控)

        var mirror = PlayerStateSO.Instance;
        if (mirror == null) return;   // 资产缺失:PlayerStateSO 自己已报过错
        if (mirror.LastSyncedTime <= 0f || Time.time - mirror.LastSyncedTime > MaxSnapshotAgeSeconds) return;

        Vector3 pos = mirror.Position;
        bool underwater = pos.y < waterLevelY;

        // 进入本关时的位姿(兜底链最后一环):玩家在本关到位(稳定点)后,第一个"水面以上"的镜像帧记一次,
        // 此后不再更新 —— 那一刻玩家已被放到出生点 / 从门里走出 / 读档恢复完,即"进入场景时的情况"。
        if (!hasEntryPose && !underwater && IsPlayerPlacedInLevel())
            RecordEntryPose(mirror);

        // 下穿水位线的瞬间 = 判定落水。一直泡着不重复触发(wasUnderwater 保持 true,
        // 只有回到水面上方后才会重新武装 —— 成功回送必然回水上方,见 RescueRoutine 收尾)。
        if (underwater && !wasUnderwater && !rescuing)
            StartCoroutine(RescueRoutine());
        wasUnderwater = underwater;

        // 安全点记录:只在"趴着 + 高于水位线"时记。飞行 / 坠落 / 水下趴一律不记。
        BeeFlightController.BeeState state = mirror.MovementState;
        bool supported = state == BeeFlightController.BeeState.Crawling;
        if (supported && pos.y > waterLevelY)
        {
            if (prevState != BeeFlightController.BeeState.Crawling)
            {
                // 刚从空中落到表面(或镜像刚就绪):立即记一次,不干等 5 秒
                RecordSafePoint();
                recordTimer = 0f;
            }
            else
            {
                recordTimer += Time.deltaTime;
                if (recordTimer >= recordIntervalSeconds)
                {
                    recordTimer = 0f;
                    RecordSafePoint();
                }
            }
        }
        else
        {
            recordTimer = 0f;   // 离地 / 入水:周期计时作废,下次落地重新起算
        }
        prevState = state;
    }

    /// <summary>记录当前趴点 + 当时的视线水平角(镜像 LookRotation)。</summary>
    private void RecordSafePoint()
    {
        var mirror = PlayerStateSO.Instance;
        if (mirror == null) return;
        safePoint = new PoseYaw
        {
            position = mirror.Position,
            yawDegrees = YawOf(mirror.LookRotation),
        };
        hasSafePoint = true;
    }

    /// <summary>记录"进入本关时的位姿"= 本关到位后第一帧的水面以上镜像(位置 + 视线水平角)。</summary>
    private void RecordEntryPose(PlayerStateSO mirror)
    {
        entryPose = new PoseYaw
        {
            position = mirror.Position,
            yawDegrees = YawOf(mirror.LookRotation),
        };
        hasEntryPose = true;
    }

    /// <summary>
    /// 玩家是否已在本关到位(可以记"进入场景时的情况")。过渡系统在场时 = 本关已进入稳定点
    /// (加载 / 传递 / 卸载都结束,玩家已被放到出生点 / 从门里走出 / 读档恢复完);
    /// 开发者单开房间场景(无 LevelTransitionManager)视为已就绪 —— 首次拿到新鲜镜像即可记。
    /// </summary>
    private static bool IsPlayerPlacedInLevel()
    {
        var manager = LevelTransitionManager.Instance;
        return manager == null || manager.IsSettled;
    }

    /// <summary>视线旋转 → 水平角(与 BeeFlightController.TeleportTo / RestoreFromSnapshot 同式分解 yaw)。</summary>
    private static float YawOf(Quaternion lookRotation)
    {
        Vector3 fwd = lookRotation * Vector3.forward;
        return Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
    }

    /// <summary>
    /// 救援流程:锁输入 → 渐黑 → 回送(全黑时传送,遮住瞬间) → 渐出 → 解锁输入。
    /// 玩家引用在触发瞬间惰性解析(tag "Player")—— 镜像新鲜 ⇒ 玩家场景必然在,
    /// 解析失败只可能是卸载竞态,报错后保持 underwater 锁停用救援(不黑屏死循环)。
    /// 无锚点(玩家从未趴过)按就近朝上的面 → 进入本关时的位姿依次兜底;三处都没有才报错不回送,
    /// 留在原地黑幕揭开。
    /// </summary>
    private IEnumerator RescueRoutine()
    {
        rescuing = true;

        BeeFlightController flight = ResolvePlayerFlight();   // 回送动作的唯一"动玩家"入口
        if (flight != null) flight.InputLocked = true;

        yield return FadeOverlay.FadeIn(fadeInSeconds);

        bool teleported = false;
        if (flight == null)
        {
            Debug.LogError("[DrownReturn] 判定落水但找不到玩家(tag \"Player\")—— 镜像新鲜却无人可回送,卸载竞态?本次不回送。", this);
        }
        else if (hasSafePoint)
        {
            // resetVelocity = true:清掉坠落 / 入水速度,从锚点静止重来。
            // 锚点不变量保证在水面以上(记录时已过滤),回送后不会立刻又判定落水。
            flight.TeleportTo(safePoint.position, safePoint.yawDegrees, resetVelocity: true);
            teleported = true;
        }
        else if (TryFindNearestUpwardSurface(flight.transform, out Vector3 landPoint))
        {
            // 从未趴过(无锚点)但附近有朝上的面:放到该面顶上(球心必在水面以上,见方法内过滤)。
            // 保持当前视角(不传 yaw);落地瞬间 Update 的"落地立即记录"会自动重记锚点。
            flight.TeleportTo(landPoint, yawDegrees: null, resetVelocity: true);
            teleported = true;
            Debug.Log("[DrownReturn] 判定落水时没有趴点锚点:已回送最近朝上的面,落地后自动重记锚点。", this);
        }
        else if (hasEntryPose)
        {
            // 附近确实没有任何水面以上的朝上面(如掉进开阔海):送回"进入本关时的位姿" ——
            // 本关唯一已知安全的落点(玩家被放到出生点 / 从门里走出 / 读档恢复完的那一刻,
            // 记录时已过滤水面以下),回送后不会立刻又判定落水。
            flight.TeleportTo(entryPose.position, entryPose.yawDegrees, resetVelocity: true);
            teleported = true;
            Debug.Log("[DrownReturn] 落水点附近搜不到朝上的面:已送回进入本关时的位姿。请检查水面附近是否有平台 / 甲板顶面。", this);
        }
        else
        {
            Debug.LogError("[DrownReturn] 判定落水但没有安全点:附近 " + rescueSearchRadius + "m 内没有朝上的可落脚面,也没记到进入本关时的位姿(玩家从未在本关到位 / 关卡一直没进稳定点) —— 本次不回送。请检查 waterLevelY 与关卡入口。", this);
        }

        yield return FadeOverlay.FadeRoutine(0f, fadeOutSeconds);

        if (flight != null) flight.InputLocked = false;   // 已被卸载 = Unity null,自动跳过
        rescuing = false;

        // underwater 边沿锁复位:回送成功 = 已回水上方 → 解除(重新武装);
        // 回送失败 = 玩家仍在水中 → 保持锁,避免每帧重放黑屏(错误已报,留给开发者修)。
        wasUnderwater = !teleported;
    }

    /// <summary>
    /// 兜底救援点搜索(仅无锚点时调用):以落水点(镜像位置)为球心向上半球球扫
    /// (16 方位 × 5 仰角 + 正上方,探测球半径 = 玩家球 + 离缝),返回"朝上 + 贴面放下后
    /// 球心仍高出水位线"的最近一处面顶。
    /// 只认朝上的面(命中法线竖直分量 ≥ minUpwardNormalY):侧壁 / 平台下沿放上去球心悬在
    /// 面旁 / 面下,没有侧向力把蜜蜂推过去 → 一路坠落再落水,救援空转(详见类注释)。
    /// 每个方位一次球扫取回全部命中再逐个筛(NonAlloc,不分配):垂直船壳挡在射线前面时,
    /// "只看第一个命中"会把船壳背后的甲板顶面永久遮蔽 —— 那恰恰是最该被找到的救援点。
    /// 命中面是触发器 / 自身碰撞体 / 起点内壁一律跳过。放置点 = 命中点 + 法线 × (半径 + 离缝),
    /// 法线朝上 ⇒ 球心落在面顶上方几厘米,下一物理步落上去即趴下 → Update 立即重记锚点
    /// (见"落地瞬间记录")。
    /// </summary>
    private bool TryFindNearestUpwardSurface(Transform playerRoot, out Vector3 landingPoint)
    {
        landingPoint = default;
        var mirror = PlayerStateSO.Instance;
        if (mirror == null || rescueSearchRadius <= 0f) return false;

        Vector3 origin = mirror.Position;   // 落水点(下穿瞬间的镜像位置,只略低于水面,足够作扫描球心)
        float castRadius = BeeColliderRadius + SurfacePlaceGap;
        float bestDistance = float.MaxValue;
        bool found = false;
        RaycastHit bestHit = default;

        // 对每个候选方向:取回全部命中逐个体检,保留"离落水点最近"的一处
        void Consider(Vector3 dir)
        {
            int count = Physics.SphereCastNonAlloc(origin, castRadius, dir, surfaceHits, rescueSearchRadius,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = surfaceHits[i];
                if (hit.distance < MinCastDistance) continue;                                // 起点已在表面内 / 自身碰撞体
                if (hit.distance >= bestDistance) continue;                                  // 比已选中的还远:不必再体检
                if (hit.normal.y < minUpwardNormalY) continue;                               // 非朝上的面(侧壁 / 下沿):放上去趴不住
                if (hit.collider.transform == playerRoot || hit.collider.transform.IsChildOf(playerRoot)) continue;
                Vector3 center = hit.point + hit.normal * castRadius;                        // 贴着放下后的球心
                if (center.y <= waterLevelY + MinClearanceYAboveWater) continue;             // 仍在水下(船壳水下段等)
                bestDistance = hit.distance;
                bestHit = hit;
                found = true;
            }
        }

        for (int azimuth = 0; azimuth < AzimuthCount; azimuth++)
        {
            float azi = azimuth * (360f / AzimuthCount);
            for (int i = 0; i < ElevationsDegrees.Length; i++)
                Consider(Quaternion.Euler(ElevationsDegrees[i], azi, 0f) * Vector3.forward);
        }
        Consider(Vector3.up);   // 正上方一发:头顶的平台顶面

        if (!found) return false;
        landingPoint = bestHit.point + bestHit.normal * castRadius;   // 法线朝上 ⇒ 落点必在该面顶上
        return true;
    }

    /// <summary>回送瞬间惰性解析玩家控制器(tag "Player";对玩家做动作的关卡侧组件的统一拿法,见 PortalDoor)。</summary>
    private static BeeFlightController ResolvePlayerFlight()
    {
        var go = GameObject.FindGameObjectWithTag("Player");
        return go != null ? go.GetComponent<BeeFlightController>() : null;
    }
}
