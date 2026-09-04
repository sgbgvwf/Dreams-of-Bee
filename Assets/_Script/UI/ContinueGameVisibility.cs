using UnityEngine;

/// <summary>
/// 主菜单"继续游戏"按钮的显隐控制。**组件挂在按钮的上一级(常驻的父物体)上**,
/// target 引用按钮本身 —— 隐藏 = target.SetActive(false),组件本体不随按钮失活,
/// 因此条件恢复时还能继续检查并重新显示(挂在按钮本体上会把自己关死)。
///
/// 规则:上次记录存在且仍在进行中(未通关)→ 按钮显示;
///       无上次记录 / 已通关 / 内容已清 → 按钮隐藏(继续无意义,应去走存档系统)。
/// 判断直接读 GameFlowManager.CanContinueLastGame;每帧轻量检查,
/// 页面停留期间删档 / 覆盖也能即时隐藏;父物体重新启用时 OnEnable 立即对齐一次。
/// </summary>
public class ContinueGameVisibility : MonoBehaviour
{
    [SerializeField, Tooltip("继续游戏按钮(要控制显示 / 隐藏的那个物体)。组件本体请挂在它的父级上。")]
    private GameObject target;

    private void OnEnable()
    {
        Apply();
    }

    private void Update()
    {
        Apply();
    }

    private void Apply()
    {
        if (target == null) return;
        bool visible = GameFlowManager.CanContinueLastGame;
        if (target.activeSelf != visible)
            target.SetActive(visible);
    }
}
