using UnityEngine;

/// <summary>
/// 主菜单按钮的"行为转接列表":所有行为按钮(开始游戏 / 继续 / 进关 / 退出)统一绑这里,
/// 再转到流程 / 场景系统(GameFlowManager / LevelTransitionManager —— 它们是 DDOL 对象,
/// 场景按钮的持久调用拖不到,所以在这里做一层可拖绑的转接)。
///
/// 页面开关不在这里:每个子页面(存档页、槽选择页…)自己挂一个 PageToggle,
/// 页面按钮直接绑那个页面的 Enable / Disable。
/// </summary>
public class MainMenuUI : MonoBehaviour
{
    /// <summary>存档系统页的槽按钮统一绑这里(载荷:槽号 0/1/2),自动判定:
    /// 空槽 → 在该槽开新游戏;有档 → 读该槽继续(已通关档由系统自动开新周目)。</summary>
    public void SlotClicked(int slot)
    {
        var s = SaveSystem.DescribeSlot(slot);
        if (!s.exists)
            GameFlowManager.StartNewGame(slot);          // 空槽:新游戏
        else
            GameFlowManager.ContinueGameAtSlot(slot);    // 有档:读档(已通关档内部转新周目)
    }

    /// <summary>继续游戏:恢复最后游玩的存档并回到它所在的关。</summary>
    public void ContinueGame()
    {
        GameFlowManager.ContinueLastGame();
    }

    /// <summary>直接进入指定关卡场景(载荷:关卡下标,0 = 第 1 关)。调试 / 自定入口;
    /// 会绕过存档会话(不自动存档),正常开局请走存档系统页(槽的 SlotClicked / 用新游戏覆盖)。</summary>
    public void EnterLevel(int levelIndex)
    {
        var ltm = LevelTransitionManager.Instance;
        if (ltm != null)
            ltm.BeginRun(levelIndex);
        else
            Debug.LogWarning("[MainMenuUI] EnterLevel: 找不到 LevelTransitionManager(未从 Persistance 启动?)");
    }

    /// <summary>退出游戏(编辑器里 = 退出播放)。</summary>
    public void QuitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }
}
