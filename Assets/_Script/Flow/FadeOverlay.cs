using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 流程切换的全屏淡入淡出黑幕(主菜单 ↔ 游玩 ↔ 结局之间遮断画面,由 GameFlowManager 使用)。
/// UI 场景化后只留行为:挂在 Persistance 场景 "Fade Canvas"(sortingOrder 100)的 GameObject 上,
/// 黑幕 Image 由场景摆放并绑定到 overlay 字段;静态 Instance 复用。
/// 淡入淡出用 WaitForSecondsRealtime —— 全项目唯一允许真实时间的等待:
/// 淡幕属于流程层,即使暂停(timeScale = 0)或加载中也必须能走完,不能让流程卡在半黑屏。
///
/// 用法(流程协程内):
///   yield return FadeOverlay.FadeInAndHold(0.35f);     // 盖幕到全黑并停留 holdBlackSeconds(切换期间遮断画面)
///   ...切换场景 / UI...
///   yield return FadeOverlay.FadeRoutine(0f, 0.4f);    // 揭开
/// 黑幕 Image raycastTarget = true:切换期间的点击不会漏到下层。
/// 开发者直玩(无 Persistance)→ Instance 为 null,所有方法空转,不影响调试。
/// </summary>
public class FadeOverlay : MonoBehaviour
{
    public static FadeOverlay Instance { get; private set; }

    [SerializeField, Tooltip("场景里的全屏黑幕 Image(sortingOrder 100 的 Fade Canvas 下)。")]
    private Image overlay;

    [SerializeField, Tooltip("盖幕后保持全黑的时长(秒):全项目黑屏节奏的唯一旋钮 —— 加载很快时黑幕一闪而过,\n这段停留保证每次切换都有一段可见的黑屏断点(0 = 盖完立即继续)。")]
    private float holdBlackSeconds = 0.5f;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>立即把黑幕设到指定透明度(0 = 完全透明,1 = 全黑)。
    /// 射线同步开关:全透明时不挡下层点击(淡出完成后主菜单可点),有任何遮挡时挡住(切换遮断)。
    /// </summary>
    public void SetAlpha(float alpha)
    {
        if (overlay == null) return;
        float a = Mathf.Clamp01(alpha);
        overlay.color = new Color(0f, 0f, 0f, a);
        overlay.raycastTarget = a > 0.001f;
    }

    /// <summary>黑幕当前是否完全透明(画面未遮挡)。</summary>
    public bool IsClear => overlay == null || overlay.color.a <= 0.001f;

    /// <summary>淡到目标透明度(协程可被流程 yield;真实时间,时长不受 timeScale 影响)。</summary>
    public static IEnumerator FadeRoutine(float targetAlpha, float duration)
    {
        var inst = Instance;
        if (inst == null || inst.overlay == null)
        {
            yield break;
        }
        float start = inst.overlay.color.a;
        float t = 0f;
        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            inst.SetAlpha(Mathf.Lerp(start, targetAlpha, Mathf.Clamp01(t / Mathf.Max(0.001f, duration))));
            yield return null;
        }
        inst.SetAlpha(targetAlpha);
    }

    /// <summary>
    /// 盖幕并停留片刻 —— 流程切换的推荐盖幕入口:淡到全黑,再按 holdBlackSeconds 保持全黑后放行。
    /// 场景加载很快时黑幕几乎瞬间完成,没有这段停留就"一闪而过"看不出切换;
    /// 黑屏时长想全局加减只调场景上本组件的 holdBlackSeconds(0 = 盖完立即继续,旧节奏)。
    /// </summary>
    public static IEnumerator FadeInAndHold(float fadeDuration)
    {
        yield return FadeRoutine(1f, fadeDuration);
        var inst = Instance;
        if (inst == null || inst.holdBlackSeconds <= 0f) yield break;
        yield return new WaitForSecondsRealtime(inst.holdBlackSeconds);
    }

    /// <summary>淡入(画面被淡幕盖住) — 语义糖,等价 FadeRoutine(1, duration)。</summary>
    public static IEnumerator FadeIn(float duration) => FadeRoutine(1f, duration);

    /// <summary>淡出(画面揭开) — 语义糖,等价 FadeRoutine(0, duration)。</summary>
    public static IEnumerator FadeOut(float duration) => FadeRoutine(0f, duration);
}
