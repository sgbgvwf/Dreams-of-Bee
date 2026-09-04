using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 暂停输入(挂任意常驻物体,如 Persistance / 玩家场景):
/// 满足两个条件才响应 Esc —— ① 状态允许(游玩中 / 开发者直玩;主菜单、结局忽略);
/// ② 暂停组件在位(PauseMenu.Instance 存在)。
/// 第一次 Esc → 暂停;第二次 Esc → 退出暂停。
///
/// 按键走 Input System:Inspector 把 Settings/Input Action.inputactions 拖给 inputActions,
/// 脚本取其中名为 "Pause" 的按钮动作(Esc 绑定已加在该资产里);没拖资产时自动用等价的
/// 临时动作兜底(开发者直玩等场景不拖也能暂停)。
/// </summary>
public class PauseInput : MonoBehaviour
{
    [SerializeField, Tooltip("Input Action 资产(Settings/Input Action.inputactions,需含名为 Pause 的按钮动作)。留空 = 临时动作兜底。")]
    private InputActionAsset inputActions;

    private InputAction pauseAction;

    private void OnEnable()
    {
        if (inputActions != null)
            pauseAction = inputActions.FindAction("Pause");

        if (pauseAction == null)
        {
            if (inputActions != null)
                Debug.LogWarning("[PauseInput] 资产里没有名为 Pause 的动作,已用临时 Esc 动作兜底。", this);
            pauseAction = new InputAction("Pause", InputActionType.Button, "<Keyboard>/escape");
        }

        pauseAction.performed += OnPausePressed;
        pauseAction.Enable();
    }

    private void OnDisable()
    {
        if (pauseAction == null) return;
        pauseAction.performed -= OnPausePressed;
        pauseAction.Disable();
        pauseAction = null;
    }

    private void OnPausePressed(InputAction.CallbackContext context)
    {
        if (PauseMenu.Instance == null) return;   // 暂停组件未就位(无菜单可开)
        if (!CanPause()) return;                  // 主菜单 / 结局不响应 Esc

        // 第一次 Esc 暂停,第二次 Esc 退出暂停
        if (PauseMenu.IsPaused) PauseMenu.Instance.Resume();
        else PauseMenu.Instance.Pause();
    }

    /// <summary>Esc 只属于游玩:主菜单 / 结局被忽略(读运行状态镜像 FlowStateSO)。</summary>
    private static bool CanPause()
    {
        var so = FlowStateSO.Instance;
        if (so != null) return so.PauseAllowed;
        // 镜像缺失(资产没建 / 流程未创建)时退化为旧行为:无流程(直玩)可暂停,有流程只限游玩
        if (GameFlowManager.Instance == null) return true;
        var s = GameFlowManager.State;
        return s == FlowState.InGame || s == FlowState.DevDirectPlay;
    }
}
