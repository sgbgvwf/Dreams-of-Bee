using UnityEngine;

/// <summary>
/// 观察物（纸张 / 书本 / 海报通用，可交互功能脚本）：实现 IInteractable 接入交互框架，
/// 右键交互 → ReadingOverlay 把 pageImage 内容放大展示（半黑背景、模拟阅读过程）。
/// 物品本身的 3D 位置 / 姿态始终不变 —— 界面是纯 UI 浮层，不做拉近物体、不做拉物件到面前。
///
/// 展示数据都归本组件（物体自己）管：
///   - pageImage：阅读时展示的内容图（可与 3D 模型表面贴图不同 —— 模型面放低清示意，阅读给高清扫描）；
///   - displaySize：内容被放大到的目标宽×高（画布单位，Canvas 参考分辨率 1920×1080 下 ≈ 屏幕像素）。
///     ReadingOverlay 打开时把内容组件尺寸设成这个规格 —— 不做任何按屏幕的自动缩放，
///     多大合适 / 屏幕外不留边等，由作者在每件物品上直接定。
///   规格的宽高比应与内容图一致（OnValidate 会警告不一致 = 会被拉伸变形）；
///   0 = 没配（运行时警告一次、不开界面）。
///
/// 与框架的关系：描边由 BeeInteractionController 的瞄准射线统一给（实现 IInteractable
/// 即被描边），右键按下由 TryInteract 调 OnInteract()。本组件不自持输入、不发射线，
/// 只提供"请求打开阅读"这个入口；阅读会话本体（冻结玩家、展示、右键退出）在
/// ReadingOverlay（Persistance 常驻）里，与其它同类物品共用一份。
///
/// 阅读中的交互语义（ReadingOverlay 类注释有完整竞态说明）：
///   - 打开期间玩家移动 / 视角被冻结（BeeFlightController.InputLocked），再次右键任意位置退出；
///   - 阅读中按 Esc = 直接进暂停页（暂停菜单盖在阅读层上），Resume 后回到阅读。
///
/// 挂法：挂在物品本体或有 Collider 的链上（没有 Collider 无法被瞄准射线命中）。
/// 与拾取物（Interactable）/ 别的 IInteractable 不要共挂一条瞄准链
/// （同链按一次分发给谁不确定，OnValidate 会警告）。
///
/// 观察无持久状态，不参与存档（ISceneSaveable 无需实现）。
/// 直玩房间未载 Persistance 时 ReadingOverlay.Instance 为 null → 交互无事发生
/// （仿 FadeOverlay 的空转约定，不影响调试）。
/// </summary>
public class PaperReading : MonoBehaviour, IInteractable
{
    [Header("展示内容")]
    [SerializeField, Tooltip("右键观察时放大的内容图（纸张/书页/海报画面）。可与模型表面贴图不同；留空 = 配置错误。")]
    private Texture2D pageImage;
    [SerializeField, Tooltip("内容被放大到的目标宽×高（画布单位，Canvas 参考分辨率 1920×1080 下 ≈ 屏幕像素）。\n阅读界面打开时内容组件尺寸 = 本规格，不做屏幕自动缩放。宽高比应与 pageImage 一致（不一致会被拉伸变形）。")]
    private Vector2 displaySize = new Vector2(1200f, 1600f);

    private bool warnedMissingImage;    // 运行时"未配图"只警告一次
    private bool warnedBadDisplaySize;  // 运行时"规格没配"只警告一次

    /// <summary>要展示的内容图（ReadingOverlay 打开会话时读取）。</summary>
    public Texture2D PageImage => pageImage;

    /// <summary>内容放大到的目标尺寸（画布单位，ReadingOverlay 打开时按此设内容组件尺寸）。</summary>
    public Vector2 DisplaySize => displaySize;

    /// <summary>交互框架入口（右键按下）：请求打开全屏阅读界面。未配图 / 规格没配 / 无常驻阅读层 → 警告一次并忽略。</summary>
    public void OnInteract()
    {
        if (pageImage == null)
        {
            if (!warnedMissingImage)
            {
                warnedMissingImage = true;
                Debug.LogWarning($"[PaperReading] {name}: 未配置 pageImage —— 没有可展示的内容。请在 Inspector 拖入纸张/书本/海报的内容图", this);
            }
            return;
        }
        if (displaySize.x <= 0f || displaySize.y <= 0f)
        {
            if (!warnedBadDisplaySize)
            {
                warnedBadDisplaySize = true;
                Debug.LogWarning($"[PaperReading] {name}: displaySize 有轴为 0 —— 内容会被缩没。请配成目标宽×高（画布单位）", this);
            }
            return;
        }

        var overlay = ReadingOverlay.Instance;
        if (overlay == null) return;   // Persistance 未载（开发者直玩）：无常驻阅读层，无事发生（仿 FadeOverlay 空转）
        overlay.OpenReading(this);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (GetComponentInParent<Interactable>() != null)
            Debug.LogWarning($"[PaperReading] {name}: 与 Interactable（可拾取物）挂在一起 —— 拾取流程会优先接管，右键会变成拾取而不是阅读。请去掉其一", this);
        var other = GetComponentInParent<IInteractable>();
        if (other != null && !ReferenceEquals(other, this))
            Debug.LogWarning($"[PaperReading] {name}: 与另一个 IInteractable（按式交互物）在同一条瞄准链上 —— 按一次分发给谁不确定。观察物应自成一体，不共链", this);
        if (pageImage == null)
            Debug.LogWarning($"[PaperReading] {name}: 未配置 pageImage —— 右键时没有内容可展示。请拖入纸张/书本/海报的内容图", this);
        if (displaySize.x <= 0f || displaySize.y <= 0f)
            Debug.LogWarning($"[PaperReading] {name}: displaySize 有轴为 0 —— 内容会被缩没。请配成目标宽×高（画布单位）", this);
        if (pageImage != null && displaySize.x > 0f && displaySize.y > 0f)
        {
            float texAspect = (float)pageImage.width / pageImage.height;
            float specAspect = displaySize.x / displaySize.y;
            if (Mathf.Abs(texAspect - specAspect) > 0.02f)
                Debug.LogWarning($"[PaperReading] {name}: displaySize 宽高比 ({displaySize.x}×{displaySize.y}) 与 pageImage 不一致（贴图 {pageImage.width}×{pageImage.height}）—— 展示时会被拉伸变形。请按贴图宽高比配规格", this);
        }
    }
#endif
}
