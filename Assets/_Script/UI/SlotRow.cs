using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 存档槽行(存档页里每个槽一行,挂一行一个,Inspector 里指定 slotIndex = 0/1/2)。职责:
///
/// 一、数据刷新(自动):按槽的存档更新"创建时间 / 游玩时长"两个 TMP 文本,
///     并检查 / 控制"继续 / 删除"按钮的可交互性(空槽禁用)。
///
/// 二、请求转接(行上的"覆盖 / 删除"按钮绑这里):
///     它们把"哪个槽、要做什么"写进静态请求(PendingSlot / PendingKind),再打开共享确认页;
///     真正的执行由确认页组件(SlotConfirmPage.Confirm)按这个请求完成 ——
///     确认页只有一份、任何一行都能用,数据通过静态请求传递。
/// </summary>
public class SlotRow : MonoBehaviour
{
    /// <summary>等待确认的动作。</summary>
    public enum PendingAction { None, Overwrite, Delete }

    /// <summary>当前等待确认的动作(确认页读它;None = 无)。</summary>
    public static PendingAction PendingKind = PendingAction.None;
    /// <summary>当前等待确认的槽(确认页读它;-1 = 无)。</summary>
    public static int PendingSlot = -1;

    /// <summary>场景里全部行(确认页删档后用来刷新文本)。</summary>
    private static readonly System.Collections.Generic.List<SlotRow> rows =
        new System.Collections.Generic.List<SlotRow>();

    // === 场景引用 ===
    [SerializeField, Tooltip("这个行对应哪个槽(0/1/2)。")]
    private int slotIndex;
    [SerializeField, Tooltip("创建时间文本(TMP;空槽时脚本会写 0000.00.00)。")]
    private TextMeshProUGUI createdText;
    [SerializeField, Tooltip("游玩时长文本(TMP;空槽时脚本会写 00:00:00)。")]
    private TextMeshProUGUI playtimeText;
    [SerializeField, Tooltip("继续游戏按钮(空槽时自动禁用)。")]
    private Button continueButton;
    [SerializeField, Tooltip("删除按钮(空槽时自动禁用)。")]
    private Button deleteButton;
    [SerializeField, Tooltip("覆盖二次确认页(所有行都拖同一个覆盖页)。行上的\"用新游戏覆盖\"按钮发起请求后打开它。")]
    private GameObject confirmOverwritePage;
    [SerializeField, Tooltip("删除二次确认页(所有行都拖同一个删除页)。行上的\"删除\"按钮发起请求后打开它。")]
    private GameObject confirmDeletePage;

    private void OnEnable()
    {
        if (!rows.Contains(this)) rows.Add(this);
        Refresh();
    }

    private void OnDisable()
    {
        rows.Remove(this);
    }

    /// <summary>按槽的实际存档刷新文本与按钮可交互性(页面打开 / 删档后自动调用)。</summary>
    public void Refresh()
    {
        var s = SaveSystem.DescribeSlot(slotIndex);

        if (createdText != null)
            createdText.text = FormatDate(s.createdTicks);   // 空档 ticks = 0 → 0000.00.00
        if (playtimeText != null)
            playtimeText.text = FormatPlay(s.playSeconds);   // 空档 0 → 00:00:00

        if (continueButton != null) continueButton.interactable = s.exists;   // 空槽不能继续
        if (deleteButton != null) deleteButton.interactable = s.exists;       // 空槽没有可删的
    }

    /// <summary>刷新全部行(确认页删除的是别的槽时,删除后文本联动)。</summary>
    public static void RefreshAll()
    {
        foreach (var row in rows)
            if (row != null) row.Refresh();
    }

    // ==================== 动作转接(按钮绑到本行组件) ====================

    /// <summary>继续游戏:读本槽进游戏(已通关档系统自动转新周目)。</summary>
    public void ContinueGame()
    {
        GameFlowManager.ContinueGameAtSlot(slotIndex);
    }

    /// <summary>行上的"用新游戏覆盖"按钮绑这里:记录请求并打开覆盖确认页。</summary>
    public void RequestOverwrite()
    {
        PendingSlot = slotIndex;
        PendingKind = PendingAction.Overwrite;
        if (confirmOverwritePage != null) confirmOverwritePage.SetActive(true);
        else Debug.LogWarning($"[SlotRow] 槽 {slotIndex}:没有绑定覆盖确认页(confirmOverwritePage 字段为空)");
    }

    /// <summary>行上的"删除"按钮绑这里:记录请求并打开删除确认页。</summary>
    public void RequestDelete()
    {
        PendingSlot = slotIndex;
        PendingKind = PendingAction.Delete;
        if (confirmDeletePage != null) confirmDeletePage.SetActive(true);
        else Debug.LogWarning($"[SlotRow] 槽 {slotIndex}:没有绑定删除确认页(confirmDeletePage 字段为空)");
    }

    /// <summary>确认页 Cancel / 执行完毕后清请求。确认页组件也会调。</summary>
    public static void ClearPending()
    {
        PendingKind = PendingAction.None;
        PendingSlot = -1;
    }

    // ==================== 显示格式 ====================

    /// <summary>创建时间:0 → "0000.00.00";否则本地时间 "yyyy.MM.dd"。</summary>
    public static string FormatDate(long utcTicks)
    {
        if (utcTicks <= 0) return "0000.00.00";
        return new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy.MM.dd");
    }

    /// <summary>游玩时长:秒数 → "hh:mm:ss"(0 → 00:00:00)。</summary>
    public static string FormatPlay(float seconds)
    {
        if (seconds <= 0f) return "00:00:00";
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}";
    }
}
