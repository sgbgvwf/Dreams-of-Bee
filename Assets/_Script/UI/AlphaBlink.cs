using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// UI 透明度呼吸闪烁(挂在任何 Image / Text 等 Graphic 上,让它在 minAlpha ↔ maxAlpha 之间循环)。
/// 用途:按钮提示"请按这里"、警告文字闪动、装饰性呼吸光点等。
///
/// 参数都在 Inspector:
///   Target —— 要闪烁的 UI 元素(留空 = 用本组件所在物体上的 Graphic);
///   minAlpha / maxAlpha —— 透明度下限 / 上限;
///   period —— 一个完整"亮 → 暗 → 亮"周期的秒数(越大越慢)。
/// 用 Time.time 驱动 → 暂停(timeScale = 0)时闪烁冻结;主菜单里 timeScale = 1 正常闪。
/// </summary>
public class AlphaBlink : MonoBehaviour
{
    [SerializeField, Tooltip("要闪烁的 UI 元素(Image/Text…);留空自动取本物体上的 Graphic。")]
    private Graphic target;
    [SerializeField, Range(0f, 1f), Tooltip("最暗时的透明度。")]
    private float minAlpha = 0.2f;
    [SerializeField, Range(0f, 1f), Tooltip("最亮时的透明度。")]
    private float maxAlpha = 1f;
    [SerializeField, Tooltip("一个完整亮→暗→亮周期的秒数(越大越慢)。")]
    private float period = 1.2f;

    private Graphic graphic;
    private Color baseColor;

    private void Awake()
    {
        graphic = target != null ? target : GetComponent<Graphic>();
        if (graphic != null)
            baseColor = graphic.color;
    }

    private void Update()
    {
        if (graphic == null || period <= 0.001f) return;

        // 三角波:0 → 1 → 0,周期 = period(Time.time 缩放,暂停即冻结)
        float t = Mathf.PingPong(Time.time * (2f / period), 1f);

        Color c = baseColor;
        c.a = Mathf.Lerp(minAlpha, maxAlpha, t);
        graphic.color = c;
    }

    private void OnDisable()
    {
        // 停止时把透明度还原,避免残留半透明状态
        if (graphic != null && baseColor.a > 0f)
        {
            Color c = graphic.color;
            c.a = baseColor.a;
            graphic.color = c;
        }
    }
}
