using UnityEngine;

/// <summary>
/// 页面开关。挂在每个子页面(存档页、槽选择页…)自己的 Canvas / 根对象上,每个页面一个。
/// 页面上的"打开"按钮绑本组件的 Enable(),"返回 / 关闭"按钮绑本组件的 Disable() ——
/// 按钮的 onClick 直接拖本页面上的这个组件选方法即可,不需要任何参数。
/// </summary>
public class PageToggle : MonoBehaviour
{
    /// <summary>启用本页面(打开按钮绑这里)。</summary>
    public void Enable()
    {
        gameObject.SetActive(true);
    }

    /// <summary>禁用本页面(本页的"返回"键绑这里)。</summary>
    public void Disable()
    {
        gameObject.SetActive(false);
    }
}
