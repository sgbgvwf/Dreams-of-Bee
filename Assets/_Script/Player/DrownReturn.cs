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
///   2. 从未趴过 / 没记到锚点（如开局即悬空直接落水）→ 以落水点为球心球扫,
///      找"水面以上最近的可趴表面"（甲板顶 / 船侧壁 / 平台下沿都算 —— 蜜蜂趴任意面都能回体力）,
///      按命中法线贴着放下;落地瞬间 Update 会自动重记锚点,不会有第二段无锚点期;
///   3. 附近实在没有（掉进开阔海）→ 回送本关 PlayerSpawnPoint 默认出生点（作者约定出生点都在面上）;
///   4. 出生点也没摆 → 配置错误,LogError 一次不回送（与"回送失败保持锁"同理,不黑屏死循环）。
/// 兜底点一律保证球心高于水位线（出生点按作者约定在水面上）,不会回送后立刻又判落水。
///
/// 救援演出:黑幕用常驻的 FadeOverlay(开发者直玩无 Persistance 时其静态方法空转 → 无黑幕瞬移)。
///
/// 用法:在关卡里给水域物体(Ocean)加本组件,把 waterLevelY 填成水面世界 Y
/// (Room_03 的 Ocean 无限平面 y = 3.34)。水位线是显式数值、不自动读场景 ——
/// 以后挪了 Ocean 记得同步改 waterLevelY。
/// 若锚点所在表面之后被移走(如滑门开门),回送后蜜蜂会从原处坠落 ——
/// 但锚点每 5 秒 / 每次落地都在刷新,下一站很快被记录,不会卡死在坏点上。
/// </summary>
public class DrownReturn : MonoBehaviour
{
    /// <summary>镜像快照允许的最大年龄(秒):超过视为玩家已卸载,停止判定(不误判缺省值 / 陈旧数据)。</summary>
    private const float MaxSnapshotAgeSeconds = 0.5f;

    [Header("落水判定")]
    [SerializeField, Tooltip("水位线(世界 Y)。玩家球心低于此高度即判定落水 —— Room_03 的 Ocean 水面 y = 3.34。")]
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
    [SerializeField, Tooltip("兜底救援半径(米):以落水点为球心向四周 8 方位 × 4 仰角 + 正上方球扫,\n找水面以上的最近接触面(甲板顶 / 船侧壁 / 平台下沿都算),按命中法线贴着放下。\n搜不到才送本关 PlayerSpawnPoint 出生点。")]
    private float rescueSearchRadius = 30f;

    // --- 兜底表面搜索常量 ---
    private const float BeeColliderRadius = 0.5f;    // 玩家 SphereCollider 半径(与出生点 Gizmo 参考圈一致)
    private const float SurfacePlaceGap = 0.05f;     // 贴面放置离缝(米):球心 = 命中点 + 法线 × (半径 + 离缝),悬停面上方一点点,落下即趴
    private const float MinClearanceYAboveWater = 0.15f;  // 放置后球心至少高出水位线这么多(防回送后立刻又判落水)
    private const float MinCastDistance = 0.05f;     // 小于此距离的命中视为"起点已在表面内 / 自身碰撞体",作废
    private const int AzimuthCount = 8;              // 四周球扫的方位数
    private static readonly float[] ElevationsDegrees = { 15f, 35f, 55f, 75f };  // 四周球扫的仰角(水平向上),覆盖船侧壁到平台下沿

    // --- 安全点(最后一次有效趴点的位置 + 当时的视线水平角) ---
    private struct SafePoint
    {
        public Vector3 position;
        public float yawDegrees;
    }

    private SafePoint safePoint;
    private bool hasSafePoint;   // 尚无任何趴点记录(落水但没锚点 = 配置错误,见 RescueRoutine 的报错)

    // --- 边沿与节流 ---
    private BeeFlightController.BeeState prevState = BeeFlightController.BeeState.Flying;
    private float recordTimer;   // 趴着期间距上次记录的时间
    private bool wasUnderwater;  // 上一帧球心是否已在水中(下穿边沿 → 只触发一次)
    private bool rescuing;       // 救援协程进行中(防重入)

    private void Update()
    {
        if (PauseMenu.IsPaused) return;   // 暂停菜单打开时让出(同 BeeFlightController 的 Update 门控)

        var mirror = PlayerStateSO.Instance;
        if (mirror == null) return;   // 资产缺失:PlayerStateSO 自己已报过错
        if (mirror.LastSyncedTime <= 0f || Time.time - mirror.LastSyncedTime > MaxSnapshotAgeSeconds) return;

        Vector3 pos = mirror.Position;
        bool underwater = pos.y < waterLevelY;

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

    /// <summary>记录当前趴点 + 当时的视线水平角(镜像 LookRotation,与 RestoreFromSnapshot 同式分解 yaw)。</summary>
    private void RecordSafePoint()
    {
        var mirror = PlayerStateSO.Instance;
        if (mirror == null) return;
        Vector3 fwd = mirror.LookRotation * Vector3.forward;
        safePoint = new SafePoint
        {
            position = mirror.Position,
            yawDegrees = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg,
        };
        hasSafePoint = true;
    }

    /// <summary>
    /// 救援流程:锁输入 → 渐黑 → 回送(全黑时传送,遮住瞬间) → 渐出 → 解锁输入。
    /// 玩家引用在触发瞬间惰性解析(tag "Player")—— 镜像新鲜 ⇒ 玩家场景必然在,
    /// 解析失败只可能是卸载竞态,报错后保持 underwater 锁停用救援(不黑屏死循环)。
    /// 无锚点(玩家从未趴过)同样报错不回送,留在原地黑幕揭开。
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
        else if (TryFindNearestCrawlableSurface(flight.transform, out Vector3 landPoint))
        {
            // 从未趴过(无锚点)但附近有可趴表面:按命中法线贴着放下(球心必在水面以上,见方法内过滤)。
            // 保持当前视角(不传 yaw);落地瞬间 Update 的"落地立即记录"会自动重记锚点。
            flight.TeleportTo(landPoint, yawDegrees: null, resetVelocity: true);
            teleported = true;
            Debug.Log("[DrownReturn] 判定落水时没有趴点锚点:已回送最近可攀爬面,落地后自动重记锚点。", this);
        }
        else if (TryGetSceneSpawnPoint(out PlayerSpawnPoint spawn))
        {
            // 附近确实没有任何水面以上的可趴表面(如掉进开阔海):送本关默认出生点(作者约定都在面上)。
            float spawnYaw = Mathf.Atan2(spawn.transform.forward.x, spawn.transform.forward.z) * Mathf.Rad2Deg;
            flight.TeleportTo(spawn.transform.position, spawnYaw, resetVelocity: true);
            teleported = true;
            Debug.Log("[DrownReturn] 落水点附近搜不到可攀爬面:已回送本关出生点。请检查水面附近是否有可落脚的平台。", this);
        }
        else
        {
            Debug.LogError("[DrownReturn] 判定落水但没有安全点:附近 " + rescueSearchRadius + "m 内无可攀爬面,关卡也没摆 PlayerSpawnPoint —— 本次不回送。请检查出生点 / 水面附近的平台 / waterLevelY。", this);
        }

        yield return FadeOverlay.FadeRoutine(0f, fadeOutSeconds);

        if (flight != null) flight.InputLocked = false;   // 已被卸载 = Unity null,自动跳过
        rescuing = false;

        // underwater 边沿锁复位:回送成功 = 已回水上方 → 解除(重新武装);
        // 回送失败 = 玩家仍在水中 → 保持锁,避免每帧重放黑屏(错误已报,留给开发者修)。
        wasUnderwater = !teleported;
    }

    /// <summary>
    /// 兜底救援点搜索(仅无锚点时调用):以落水点(镜像位置)为球心,向四周 8 方位 × 4 仰角 + 正上方
    /// 球扫(探测球半径 = 玩家球 + 离缝),返回"贴面放置后球心仍高出水位线"的最近命中面。
    /// 蜜蜂趴任意面都能趴住回体力(爬行时速度由脚本逐物理步设定,不会被重力拖下水),所以
    /// 甲板顶 / 船侧壁 / 平台下沿都可作救援点 —— 不必苛求"站得住的顶面";命中面是触发器 /
    /// 自身碰撞体 / 起点内壁一律跳过。探测球贴着表面放下时球心距表面 = 玩家半径 + 离缝,
    /// 悬停面上方几厘米,下一物理步落上去即趴下 → Update 立即重记锚点(见"落地瞬间记录")。
    /// </summary>
    private bool TryFindNearestCrawlableSurface(Transform playerRoot, out Vector3 landingPoint)
    {
        landingPoint = default;
        var mirror = PlayerStateSO.Instance;
        if (mirror == null || rescueSearchRadius <= 0f) return false;

        Vector3 origin = mirror.Position;   // 落水点(下穿瞬间的镜像位置,只略低于水面,足够作扫描球心)
        float castRadius = BeeColliderRadius + SurfacePlaceGap;
        float bestDistance = float.MaxValue;
        bool found = false;
        RaycastHit bestHit = default;

        // 对每个候选方向:命中后过滤,并保留"离落水点最近"的一处
        void Consider(Vector3 dir)
        {
            if (!Physics.SphereCast(origin, castRadius, dir, out RaycastHit hit, rescueSearchRadius,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return;
            if (hit.distance < MinCastDistance) return;                                   // 起点已在表面内 / 自身碰撞体
            if (hit.collider.transform == playerRoot || hit.collider.transform.IsChildOf(playerRoot)) return;
            Vector3 center = hit.point + hit.normal * castRadius;                         // 贴着放下后的球心
            if (center.y <= waterLevelY + MinClearanceYAboveWater) return;                // 仍在水下(船壳水下段等)
            if (hit.distance >= bestDistance) return;
            bestDistance = hit.distance;
            bestHit = hit;
            found = true;
        }

        for (int azimuth = 0; azimuth < AzimuthCount; azimuth++)
        {
            float azi = azimuth * (360f / AzimuthCount);
            for (int i = 0; i < ElevationsDegrees.Length; i++)
                Consider(Quaternion.Euler(ElevationsDegrees[i], azi, 0f) * Vector3.forward);
        }
        Consider(Vector3.up);   // 正上方一发:平台 / 栈桥底面等

        if (!found) return false;
        landingPoint = bestHit.point + bestHit.normal * castRadius;
        return true;
    }

    /// <summary>本组件所在关卡场景的默认出生点(无标记 = false)。关卡侧组件只认自己场景的出生点,不吃玩家场景。</summary>
    private bool TryGetSceneSpawnPoint(out PlayerSpawnPoint spawn)
    {
        spawn = PlayerSpawnPoint.FindDefault(gameObject.scene);
        return spawn != null;
    }

    /// <summary>回送瞬间惰性解析玩家控制器(tag "Player";对玩家做动作的关卡侧组件的统一拿法,见 PortalDoor)。</summary>
    private static BeeFlightController ResolvePlayerFlight()
    {
        var go = GameObject.FindGameObjectWithTag("Player");
        return go != null ? go.GetComponent<BeeFlightController>() : null;
    }
}
