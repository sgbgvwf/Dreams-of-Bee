using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 体力条数据驱动(视觉由你在场景里搭建,脚本只负责把归一化体力写进你的结构)。
/// 玩家场景只在游玩时加载 → 体力条天然只出现在游玩中,主菜单 / 结局自动不在画面。
///
/// 兑现 BeeFlightController 第 104 行的 TODO(UI):体力条 = 玩家飞行的燃料,
/// 消耗时(飞行)下降,恢复时(趴着 / 坠落)回满。
///
/// 两种填充驱动(Inspector 里 driver 选择,按你的结构配):
///   - FilledImage(默认):绑 realFill 到填充 Image,要求 Image.Type = Filled(垂直、自下而上);
///     可再加一条残影 Image 绑 ghostFill(可选,不加则无残影)。
///   - RectHeight:绑 fillRect 到"填充内容"的 RectTransform(建议锚在底部),
///     脚本按比例改 sizeDelta.y(从满高的初始值缩放)。适合"背景 + 一个填充子物体"的结构;
///     此时 realFill 仍可绑定(仅用于低体力变色,不设也行)。
///
/// 数据驱动方式:
///   - 玩家场景由 LevelTransitionManager 加载,体力条找到 BeeFlightController 之前每帧重试;
///     找到后订阅 StaminaChanged(消耗 / 恢复期每帧都有变化,事件驱动足够),并立即同步一次当前值;
///   - 事件载荷是体力原值,进度直接读 StaminaNormalized(0..1) 与控制器字段保持同源;
///   - 残影在 Update 用 Time.deltaTime 向实时值收敛 → 暂停(timeScale=0)时 deltaTime=0,
///     残影自然冻结,与 PauseMenu 的暂停机制天然一致,无需感知暂停。
///   颜色渐变(充足淡金 / 告急警示红)在代码内,想改在场景里把填充颜色固定即可不受影响。
/// </summary>
public class StaminaBarHUD : MonoBehaviour
{
    /// <summary>填充驱动方式(按场景结构选择)。</summary>
    public enum FillDriver { FilledImage, RectHeight }

    // === 场景引用(Player 场景体力条结构下绑定) ===
    [SerializeField, Tooltip("驱动方式:FilledImage = 填充 Image 的 fillAmount;RectHeight = 填充内容的 RectTransform 高度。")]
    private FillDriver driver = FillDriver.FilledImage;
    [SerializeField, Tooltip("实时填充 Image(FilledImage 模式必绑;RectHeight 模式下绑了仅用于低体力变色)。")]
    private Image realFill;
    [SerializeField, Tooltip("残影填充 Image(可选;无则不绑)。FilledImage 模式专用。")]
    private Image ghostFill;
    [SerializeField, Tooltip("RectHeight 模式:填充内容的 RectTransform(建议底部锚定;满体力时的初始高度会被记录为基准)。")]
    private RectTransform fillRect;

    private BeeFlightController flight;   // 数据源(玩家控制器与体力条同场景,懒查找)
    private float target01 = 1f;          // 实时体力(归一 0..1)
    private float ghost01 = 1f;           // 残影当前值
    private float fullHeight = 1f;        // RectHeight 模式:满体力时填充高度(启动时记录)

    private void Awake()
    {
        // 记录满高基准,并把初始视觉同步到满值
        if (driver == FillDriver.RectHeight && fillRect != null)
            fullHeight = Mathf.Max(0.01f, fillRect.sizeDelta.y);
        if (realFill != null) realFill.fillAmount = 1f;
        if (ghostFill != null) ghostFill.fillAmount = 1f;
        if (fillRect != null) fillRect.sizeDelta = new Vector2(fillRect.sizeDelta.x, fullHeight);
    }

    private void Update()
    {
        // 玩家控制器与体力条同在 Player 场景,加载顺序基本稳定;找不到时每帧重试
        if (flight == null)
        {
            flight = FindObjectOfType<BeeFlightController>();
            if (flight == null) return;
            flight.StaminaChanged += OnStaminaChanged;
            target01 = flight.StaminaNormalized;   // 订阅前先同步一次初值,不等下一次结算
            ApplyFill();
        }

        // 残影向实时值收敛:飞行消耗时慢(留在高处慢慢沉),休整恢复时快(尾巴较快合拢)
        ghost01 = Mathf.MoveTowards(ghost01, target01, GhostSettleSpeed * Time.deltaTime);
        if (ghostFill != null) ghostFill.fillAmount = ghost01;
    }

    private void OnStaminaChanged(float currentStamina)
    {
        if (flight == null) return;
        target01 = flight.StaminaNormalized;
        ApplyFill();
    }

    /// <summary>按实时体力刷新填充与颜色:高于阈值保持淡金,低于阈值渐变为警示红。</summary>
    private void ApplyFill()
    {
        if (driver == FillDriver.FilledImage)
        {
            if (realFill != null)
            {
                realFill.fillAmount = target01;
                realFill.color = FillColor;
            }
        }
        else if (fillRect != null)
        {
            fillRect.sizeDelta = new Vector2(fillRect.sizeDelta.x, fullHeight * target01);
            if (realFill != null) realFill.color = FillColor;   // 绑了才变色(可仅用于告警色)
        }
    }

    /// <summary>低体力告警渐变(充足淡金 → 告急红)。</summary>
    private Color FillColor => Color.Lerp(DangerColor, IdleColor,
        Mathf.InverseLerp(0f, DangerThreshold, target01));

    // === 表现参数(想调在场景里改 Image 颜色;这两组是运行时渐变逻辑) ===
    private static readonly Color IdleColor = new Color(1f, 0.8f, 0.34f, 1f);        // 淡金(体力充足)
    private static readonly Color DangerColor = new Color(1f, 0.24f, 0.2f, 1f);      // 警示红(体力告急)
    private const float DangerThreshold = 0.35f;            // 体力低于此比例开始渐红
    private const float GhostSettleSpeed = 0.25f;           // 残影收敛速度(0..1/s,略慢于消耗就有"尾巴")
}
