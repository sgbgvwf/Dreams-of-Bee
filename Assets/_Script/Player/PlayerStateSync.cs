using UnityEngine;

/// <summary>
/// 玩家跨场景状态镜像的"唯一写方"。挂在玩家根（Body，Player.unity 常驻场景）上，
/// 与 BeeFlightController / BeeInteractionController 同级。
///
/// 职责：每帧（LateUpdate，渲染帧末 —— 物理插值 + 相机同步都已完成，镜像即"当前画面上的玩家"）
/// 把玩家位姿 / 速度 / 运动状态 / 体力 / 视线 / 持物写入 PlayerStateSO，
/// 供其它场景的脚本通过 PlayerStateSO.Instance 零耦合读取。
///
/// 生命周期：state 是场景序列化引用 —— 引擎级强引用，保证 LevelTransitionManager 卸载关卡时调用的
/// Resources.UnloadUnusedAssets() 不会卸载该资产（静态字段引用拦不住 Resources 卸载）。
/// </summary>
public class PlayerStateSync : MonoBehaviour
{
    [SerializeField, Tooltip("玩家状态镜像资产（Resources/PlayerState/PlayerStateSO）。场景引用兼作引擎级强引用")]
    private PlayerStateSO state;

    // --- 数据源（都在玩家根上，GetComponent 自取，不改动两个控制器内部逻辑） ---
    private BeeFlightController flight;
    private BeeInteractionController interaction;
    private Rigidbody rb;

    private void Awake()
    {
        flight = GetComponent<BeeFlightController>();
        interaction = GetComponent<BeeInteractionController>();
        rb = GetComponent<Rigidbody>();
    }

    private void OnEnable()
    {
        PushSnapshot();   // 尽早写入初始值：启动时关卡场景与玩家场景并行加载，关卡脚本可先读到缺省期数据
    }

    private void LateUpdate()
    {
        PushSnapshot();
    }

    /// <summary>把玩家当前状态写入 PlayerStateSO（空引用任一缺失则跳过该帧，不刷错）。</summary>
    private void PushSnapshot()
    {
        if (state == null || flight == null || interaction == null || rb == null) return;

        // 持物镜像的不变量：空手 ⇒ 物品 Id 恒为空。持物随场景卸载被销毁时
        // IsHolding 立刻变 false，而身份字段要到控制器检测到销毁后才清 —— 这里按
        // 持物与否裁剪 Id，保证任何一帧镜像都不会出现"空手 + 旧持物身份"的组合。
        bool holding = interaction.IsHolding;

        state.SyncSnapshot(
            transform.position, transform.rotation, transform.localScale,
            rb.velocity,
            flight.CurrentState,
            flight.Stamina, flight.MaxStamina,
            flight.CameraRotation,
            holding, holding ? interaction.HeldItemId : "");
    }
}
