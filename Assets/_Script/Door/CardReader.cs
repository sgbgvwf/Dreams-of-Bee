using UnityEngine;

/// <summary>
/// 门禁读卡器：携带的钥匙（IDoorKey，卡片只是第一个实现）进入读卡器的触发器区域 → 刷卡成功。
/// - 有 LevelTransitionManager（关卡过渡系统）时：刷卡只负责触发（T0），钥匙身份随请求
///   上报 —— 刷卡"去哪" = 卡上目的地，由管理器在刷卡瞬间读取裁决（同一扇出口门刷不同卡
///   去不同地方；卡没配目的地会报错拒绝）。门由管理器在目的地关加载 + 对齐完成后打开，
///   保证"先加载、后开门"的顺序；刷的不是当前关的出口门会被管理器拒绝（防走回头路 / 刷错门）。
/// - 无过渡系统时：保持原有行为 —— 刷卡直接开门（刷卡只开门，从不关门）。
/// - 刷卡成功会给 successLight（LightColorAlternator）亮第二种预设色（如绿色）作反馈。
///
/// 放行判定（都在读卡器侧进行）：
///   - requiredItemId 非空时只放行 KeyId 与之相同的钥匙（给钥匙配 Interactable.itemId 即可）；
///     空 = 进触发区的钥匙都放行（刷卡去哪由钥匙上的目的地决定，见 Card.cs —— 归属关卡概念
///     已删除：关卡物件随穿门卸载销毁，钥匙带不出关，无需关卡序号校验）；
///   - 管理器未进入稳定点（开局 / 读档恢复中）时静默忽略 —— 读档落回读卡区的钥匙不会自动重刷。
///
/// 配置：读卡器挂本脚本，加一个 Is Trigger 的 Collider 作为刷卡范围（"读卡器附近"），
/// 把门的 SlidingDoor 组件拖进 door 字段；钥匙需要有 Rigidbody（Is Kinematic 即可）
/// 触发事件才会生效（见 Card.cs）。
///
/// 注意：读卡器不是 Interactable —— 不可拾取、无描边提示。刷卡是纯触发式的
/// （携带钥匙进入区域即触发，见 OnTriggerEnter）。
/// </summary>
public class CardReader : MonoBehaviour
{
    [SerializeField, Tooltip("本读卡器控制的滑动门（门根上的 SlidingDoor 组件）")]
    private SlidingDoor door;

    [SerializeField, Tooltip("可选：刷卡成功时亮第二种预设色（如绿色）的 LightColorAlternator，留空自动从门的子物体查找")]
    private LightColorAlternator successLight;

    [SerializeField, Tooltip("可选：要求的钥匙身份 Id（非空时只接受 KeyId 与之相同的钥匙）。同一关多扇门分流用；留空 = 只按关卡归属放行")]
    private string requiredItemId = "";

    private bool warnedMissingDoor;

    private void OnTriggerEnter(Collider other)
    {
        // 只有带 IDoorKey 标记的物体进入才视为刷卡（Card / 未来任意钥匙物皆可）
        var key = other.GetComponentInParent<IDoorKey>();
        if (key == null) return;

        if (door == null)
        {
            if (!warnedMissingDoor)
            {
                warnedMissingDoor = true;
                Debug.LogWarning($"[CardReader] 未指定门：{name}，请把门的 SlidingDoor 拖到 Inspector", this);
            }
            return;
        }

        // 身份分流：本读卡器指定了钥匙 Id → 只认对钥匙；空 = 进触发区的钥匙都放行
        if (!string.IsNullOrEmpty(requiredItemId) && key.KeyId != requiredItemId)
        {
            Debug.Log($"[CardReader] {name}: 钥匙身份不匹配（需要 {requiredItemId}），拒绝刷卡", this);
            Deny();
            return;
        }

        SwipeSucceeded(key);
    }

    private void SwipeSucceeded(IDoorKey key)
    {
        bool accepted;
        var manager = LevelTransitionManager.Instance;
        if (manager != null)
        {
            // 管理器未进稳定点（开局 / 读档恢复中）：静默忽略 —— 恢复时落回读卡区的钥匙不自动重刷
            if (!manager.IsSettled) return;

            // 关卡过渡模式：刷卡 = T0 触发（走封装层公开入口 RequestExitKeyed），
            // 钥匙身份随请求上报 —— 目的地由卡携带、管理器在刷卡瞬间读取裁决；
            // 门由管理器在加载+对齐完成后打开（顺序由管理器保证）
            accepted = manager.RequestExitKeyed(door, key);
        }
        else
        {
            // 无过渡系统的普通用法：刷卡只开门，不关门
            door.OpenDoor();
            accepted = true;
        }

        if (!accepted)
        {
            Deny();   // 被管理器拒绝（刷错门 / 重复刷卡）:拒绝音效
            return;
        }

        // 成功反馈：显示灯的第二种预设色（绿）
        if (successLight == null && door != null)
            successLight = door.GetComponentInChildren<LightColorAlternator>(true);
        if (successLight != null) successLight.ShowColor(true);

        GameEvents.CardSuccess?.Invoke(transform.position);   // 刷卡成功音效(注册式同步)
    }

    private void Deny()
    {
        GameEvents.CardDeny?.Invoke(transform.position);   // 刷卡被拒音效(注册式同步)
    }
}
