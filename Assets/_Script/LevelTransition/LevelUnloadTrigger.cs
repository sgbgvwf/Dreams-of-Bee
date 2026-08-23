using UnityEngine;

/// <summary>
/// 卸载触发器（T3）：玩家继续往前走踏入本区域后，请求关闭上一关出口门并异步卸载上一关。
/// 纯通知组件 —— 关门动画 + 等待关门完成 + 卸载的完整流程在 LevelTransitionManager 中执行。
/// 放在通过触发器之后（门后 8~12m 处）。
/// </summary>
public class LevelUnloadTrigger : MonoBehaviour
{
    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("Player")) return;
        if (LevelTransitionManager.Instance != null)
            LevelTransitionManager.Instance.OnPlayerEnteredUnloadZone(this);
    }
}
