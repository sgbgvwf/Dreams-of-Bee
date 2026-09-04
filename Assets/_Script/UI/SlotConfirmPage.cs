using TMPro;
using UnityEngine;

/// <summary>
/// 二次确认页的确认逻辑(覆盖确认页与删除确认页是两页,各自挂一个本组件 —— 组件逻辑共用)。
/// 行上的"用新游戏覆盖 / 删除"按钮会先把请求(哪个槽、什么动作)写进
/// SlotRow 的静态请求再打开对应确认页 —— 本组件负责:
///   - 打开时刷新提示文案(如"将用新游戏覆盖槽 1"/"将删除槽 2 的存档";messageText 可留空);
///   - "确认"按钮 → Confirm(按请求执行:覆盖 = 开新局;删除 = 清槽并刷新全部行);
///   - "取消 / 返回"按钮 → Cancel(清请求并关掉本页)。
/// </summary>
public class SlotConfirmPage : MonoBehaviour
{
    [SerializeField, Tooltip("提示文本(TMP,可选):打开时自动显示\"将…槽 N\"。不绑则只有按钮没有说明文字。")]
    private TextMeshProUGUI messageText;

    private void OnEnable()
    {
        // 每次打开时按请求刷新提示
        if (messageText == null) return;
        int slot = SlotRow.PendingSlot;
        switch (SlotRow.PendingKind)
        {
            case SlotRow.PendingAction.Overwrite:
                messageText.text = $"将用新游戏覆盖槽 {slot + 1},该档进度会被清空。\n确定继续?";
                break;
            case SlotRow.PendingAction.Delete:
                messageText.text = $"将删除槽 {slot + 1} 的存档。\n确定删除?";
                break;
            default:
                messageText.text = "";
                break;
        }
    }

    /// <summary>确认页的"确认"按钮绑这里:按静态请求执行(覆盖开新局 / 删除清槽)。</summary>
    public void Confirm()
    {
        int slot = SlotRow.PendingSlot;
        var kind = SlotRow.PendingKind;
        SlotRow.ClearPending();
        gameObject.SetActive(false);

        if (slot < 0 || slot >= SaveSchema.SlotCount)
        {
            Debug.LogWarning("[SlotConfirmPage] 确认时没有待确认的请求(可能已被其它操作打断),忽略");
            return;
        }

        if (kind == SlotRow.PendingAction.Overwrite)
            GameFlowManager.StartNewGame(slot);            // 系统会清槽并开新周目
        else if (kind == SlotRow.PendingAction.Delete)
        {
            SaveSystem.ClearSlot(slot);
            SlotRow.RefreshAll();                          // 文本 / 按钮状态联动
        }
    }

    /// <summary>确认页的"取消 / 返回"按钮绑这里:清请求并关掉本页。</summary>
    public void Cancel()
    {
        SlotRow.ClearPending();
        gameObject.SetActive(false);
    }
}
