using UnityEngine;

/// <summary>
/// 通过触发器（T2）：玩家穿过门后踏入本区域。
/// 纯通知组件 —— 状态流转全部在 LevelTransitionManager 中完成。
/// 若下一关尚未加载完成（极端情况：玩家速度极快），管理器会记录等待，
/// 加载+对齐完成瞬间自动结算，全程无任何加载 UI / 进度条 / 黑屏。
/// 放在"下一关"场景里、门后 2~4m 处（下一关未加载前不可能触发）。
/// </summary>
public class LevelPassTrigger : MonoBehaviour
{
    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("Player")) return;
        if (LevelTransitionManager.Instance != null)
            LevelTransitionManager.Instance.OnPlayerEnteredPassZone(this);
    }
}
