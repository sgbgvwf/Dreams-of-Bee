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
        if (flight != null && hasSafePoint)
        {
            // resetVelocity = true:清掉坠落 / 入水速度,从锚点静止重来。
            // 锚点不变量保证在水面以上(记录时已过滤),回送后不会立刻又判定落水。
            flight.TeleportTo(safePoint.position, safePoint.yawDegrees, resetVelocity: true);
            teleported = true;
        }
        else if (flight != null)
        {
            Debug.LogError("[DrownReturn] 判定落水但没有安全点(玩家从未趴着?)—— 本次不回送。请检查出生位置 / waterLevelY。", this);
        }
        else
        {
            Debug.LogError("[DrownReturn] 判定落水但找不到玩家(tag \"Player\")—— 镜像新鲜却无人可回送,卸载竞态?本次不回送。", this);
        }

        yield return FadeOverlay.FadeRoutine(0f, fadeOutSeconds);

        if (flight != null) flight.InputLocked = false;   // 已被卸载 = Unity null,自动跳过
        rescuing = false;

        // underwater 边沿锁复位:回送成功 = 已回水上方 → 解除(重新武装);
        // 回送失败 = 玩家仍在水中 → 保持锁,避免每帧重放黑屏(错误已报,留给开发者修)。
        wasUnderwater = !teleported;
    }

    /// <summary>回送瞬间惰性解析玩家控制器(tag "Player";对玩家做动作的关卡侧组件的统一拿法,见 PortalDoor)。</summary>
    private static BeeFlightController ResolvePlayerFlight()
    {
        var go = GameObject.FindGameObjectWithTag("Player");
        return go != null ? go.GetComponent<BeeFlightController>() : null;
    }
}
