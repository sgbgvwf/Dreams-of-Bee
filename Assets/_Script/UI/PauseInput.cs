using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 暂停输入(挂常驻场景物体,如 Persistance 的 "Pause" GO):
/// 满足两个条件才响应 Esc —— ① 状态允许(游玩中 / 开发者直玩;主菜单、结局忽略);
/// ② 暂停组件在位(PauseMenu.Instance 存在)。
/// 第一次 Esc → 暂停;第二次 Esc → 退出暂停。
///
/// Esc 动作经 GameInput 取(资产常驻 Resources,动作解析见 GameInput):
/// 本组件不持有资产引用、无运行时兜底 —— 资产缺失 / 缺 Pause 动作由 GameInput 报错,
/// 这里不订阅不响应,暂停不可用会以日志形式显式暴露。
/// </summary>
public class PauseInput : MonoBehaviour
{
    private InputAction pauseAction;

    private void OnEnable()
    {
        pauseAction = GameInput.Pause;
        if (pauseAction == null) return;   // GameInput 已报错:不订阅,防 NRE
        pauseAction.performed += OnPausePressed;
    }

    private void OnDisable()
    {
        if (pauseAction == null) return;
        pauseAction.performed -= OnPausePressed;
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
