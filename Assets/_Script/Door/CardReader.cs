using UnityEngine;

/// <summary>
/// 门禁读卡器：携带的 Card（卡）进入读卡器的触发器区域 → 刷卡成功。
/// - 有 LevelTransitionManager（关卡过渡系统）时：刷卡只负责触发（T0），
///   门由管理器在下一关加载 + 对齐完成后打开，保证"先加载、后开门"的顺序；
///   刷的不是当前出口门会被管理器拒绝（防走回头路 / 刷错门）。
/// - 无过渡系统时：保持原有行为 —— 刷卡直接开门（刷卡只开门，从不关门）。
/// - 刷卡成功会给 successLight（LightColorAlternator）亮第二种预设色（如绿色）作反馈。
/// 配置：读卡器挂本脚本，加一个 Is Trigger 的 Collider 作为刷卡范围（"读卡器附近"），
/// 把门的 SlidingDoor 组件拖进 door 字段；卡需要有 Rigidbody（Is Kinematic 即可）
/// 触发事件才会生效（见 Card.cs）。
///
/// 注意：读卡器不是 Interactable —— 不可拾取、无描边提示。刷卡是纯触发式的
/// （携带卡片进入区域即触发，见 OnTriggerEnter）。
/// </summary>
public class CardReader : MonoBehaviour
{
    [SerializeField, Tooltip("本读卡器控制的滑动门（门根上的 SlidingDoor 组件）")]
    private SlidingDoor door;

    [SerializeField, Tooltip("可选：刷卡成功时亮第二种预设色（如绿色）的 LightColorAlternator，留空自动从门的子物体查找")]
    private LightColorAlternator successLight;

    private bool warnedMissingDoor;

    private void OnTriggerEnter(Collider other)
    {
        // 只有带 Card 标记的物体进入才视为刷卡
        var card = other.GetComponent<Card>();
        if (card == null) return;

        // 关卡识别：卡片只能刷它所属关卡（即当前关卡）的读卡器 ——
        // 防止上一关的卡被带进新关卡后刷开下一关的门。
        var manager = LevelTransitionManager.Instance;
        if (manager != null && card.LevelIndex >= 0 && card.LevelIndex != manager.CurrentLevelIndex)
        {
            Debug.Log($"[CardReader] {name}: 这张卡属于第 {card.LevelIndex} 关，当前是第 {manager.CurrentLevelIndex} 关，拒绝刷卡", this);
            return;
        }

        if (door == null)
        {
            if (!warnedMissingDoor)
            {
                warnedMissingDoor = true;
                Debug.LogWarning($"[CardReader] 未指定门：{name}，请把门的 SlidingDoor 拖到 Inspector", this);
            }
            return;
        }

        SwipeSucceeded();
    }

    private void SwipeSucceeded()
    {
        bool accepted;
        var manager = LevelTransitionManager.Instance;
        if (manager != null)
        {
            // 关卡过渡模式：刷卡 = T0 触发，门由管理器在加载+对齐完成后打开（顺序由管理器保证）
            accepted = manager.OnCardSwiped(door);
        }
        else
        {
            // 无过渡系统的普通用法：刷卡只开门，不关门
            door.OpenDoor();
            accepted = true;
        }

        if (!accepted) return;   // 被管理器拒绝（刷错门 / 重复刷卡）：不给成功反馈

        // 成功反馈：显示灯的第二种预设色（绿）
        if (successLight == null && door != null)
            successLight = door.GetComponentInChildren<LightColorAlternator>(true);
        if (successLight != null) successLight.ShowColor(true);
    }
}
