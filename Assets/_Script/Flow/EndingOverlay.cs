using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 结局演出幕(UI 场景化后只留行为;视觉对象在 Persistance 场景的 "Ending Canvas" 下,
/// 由场景 YAML 摆放并在此绑定)。挂在 Persistance 场景 "Ending Canvas" 的 GameObject 上,
/// 跨关卡常驻 —— 结局演出可出现在任意时刻(游玩结束直接进)。
/// 纯渲染层:文案来自 EndingCatalog 的当前定义(加结局 = 目录加条目,本文件零改动);
/// 页面 = 全屏底色层 + 标题 + 正文 + 返回主菜单。返回主菜单按下 → GameFlowManager.EndingConfirmed()
/// (结算 completedRuns / 结局入库 / 槽位置已通关)。
/// 底色层:Room_00 只做主菜单背景、不担任结局演出 —— 默认结局不加载任何场景,
/// 由底色层(纯色全屏)兜底画面;只有结局定义显式给了演出场景(backdropScenePath,自配相机)时,
/// 流程才会以"透明"显示并加载那个场景(Show 的 solidBackdrop = false)。
/// </summary>
public class EndingOverlay : MonoBehaviour
{
    /// <summary>场景实例(Persistance 常驻;GameFlowManager 经此显示结局)。</summary>
    public static EndingOverlay Instance { get; private set; }

    // === 场景引用(Persistance 场景 "Ending Canvas" 下绑定) ===
    [SerializeField, Tooltip("页面根(Ending Page:Backdrop + 标题 + 正文 + 按钮)。")]
    private GameObject root;
    [SerializeField, Tooltip("全屏底色层(solid 时盖住后面画面,透明时露出结局演出场景)。")]
    private Image backdrop;
    [SerializeField] private TextMeshProUGUI titleText;
    [SerializeField] private TextMeshProUGUI bodyText;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        Hide();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <param name="solidBackdrop">true = 用纯色全屏底(默认结局,不加载场景);false = 透明,露出结局定义指定的演出场景。</param>
    public void Show(EndingCatalog.EndingDefinition def, bool solidBackdrop)
    {
        if (backdrop != null)
        {
            var c = backdrop.color;
            backdrop.color = new Color(c.r, c.g, c.b, solidBackdrop ? 1f : 0f);
        }
        if (titleText != null) titleText.text = def.title;
        if (bodyText != null) bodyText.text = def.body;
        if (root != null) root.SetActive(true);
    }

    public void Hide()
    {
        if (root != null) root.SetActive(false);
    }

    /// <summary>"返回主菜单"按钮(场景按钮的持久 onClick 绑定)。</summary>
    public void BackToMenu()
    {
        GameFlowManager.EndingConfirmed();
    }
}
