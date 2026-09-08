using UnityEngine;

/// <summary>
/// 体力条数据驱动(高度式):脚本只做一件事 —— 按体力比例修改填充内容的 RectTransform 高度。
/// 框、颜色、位置都是你的场景里搭好的美术,脚本不碰。
///
/// 结构建议(你搭的样子):体力条根(挂本组件)→ 子物体[框 Image(静态)、填充内容(被本脚本改高)]。
/// 填充内容的 RectTransform 要求:单点锚定(anchorMin.y = anchorMax.y,不要上下拉伸),
/// 初始高度 = 满体力时的高度;pivot 随意 —— 脚本无论 pivot 在哪都保持"下缘固定、向上伸缩"
/// (体力满 = 原高度,体力 0 = 高度 0)。
///
/// 数据源:BeeFlightController(玩家场景,与体力条同在);玩家场景只在游玩时加载,
/// 主菜单 / 结局体力条天然不在画面。暂停(timeScale = 0)时体力不结算,本脚本同步冻结。
/// </summary>
public class StaminaBarHUD : MonoBehaviour
{
    [SerializeField, Tooltip("填充内容的 RectTransform(建议底部锚定 + pivot.y = 0;启动时的高度 = 满体力基准)。")]
    private RectTransform fill;

    private BeeFlightController flight;   // 数据源(懒查找)
    private float fullHeight = 1f;        // 满体力时的填充高度(启动时记录)
    private bool warned;                  // 一次性诊断(引用没绑 / 找不到玩家时只报一次)

    private void Awake()
    {
        if (fill == null)
        {
            Debug.LogWarning("[StaminaBarHUD] fill 字段没绑定(体力条不工作):把填充内容的 RectTransform 拖进来。", this);
            return;
        }
        // 要求单点锚定(不上下拉伸);满体力基准 = 摆好的初始高度
        if (Mathf.Abs(fill.anchorMin.y - fill.anchorMax.y) > 0.001f)
        {
            Debug.LogWarning("[StaminaBarHUD] 填充内容是上下拉伸锚定,高度缩放不会生效:请把 anchorMin.y / anchorMax.y 改成同一点(例如都 0,底部固定)。", this);
            return;
        }
        fullHeight = Mathf.Max(0.01f, fill.sizeDelta.y);
        Debug.Log($"[StaminaBarHUD] 满体力基准高度 = {fullHeight}(若为 0 或异常,检查填充内容的 RectTransform 摆法)", this);
    }

    private void Update()
    {
        if (fill == null) return;

        // 玩家控制器与体力条同在 Player 场景;找到之前每帧重试
        if (flight == null)
        {
            flight = FindObjectOfType<BeeFlightController>();
            if (flight == null)
            {
                if (!warned)
                {
                    warned = true;
                    Debug.LogWarning("[StaminaBarHUD] 找不到 BeeFlightController(体力条不工作):确认体力条与玩家在同一个场景(只在游玩 / 直玩玩家场景时存在)。", this);
                }
                return;
            }
        }

        // 高度目标:体力 0..1 → 0..fullHeight。无论 pivot 在哪个位置都保持"下缘固定、向上伸缩":
        // 单点锚定时 rect 下缘 = anchoredPosition.y - pivot.y * 高度 —— 改高时同步补偿
        // anchoredPosition.y,让下缘不动(例如 pivot=(0.5,0.5) 也不会再从中间对称缩放)。
        float oldH = fill.sizeDelta.y;
        float targetH = fullHeight * flight.StaminaNormalized;
        if (Mathf.Abs(oldH - targetH) <= 0.001f) return;

        float bottom = fill.anchoredPosition.y - fill.pivot.y * oldH;
        fill.sizeDelta = new Vector2(fill.sizeDelta.x, targetH);
        fill.anchoredPosition = new Vector2(fill.anchoredPosition.x, bottom + fill.pivot.y * targetH);
    }
}
