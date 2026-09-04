using UnityEngine;

/// <summary>
/// 场景转换组件 —— 每场景一个,与常驻 LevelTransitionManager / GameFlowManager 联系的薄 Facade。
///
/// 为什么需要它:场景 UI 已场景化(按钮持久 onClick 只能绑"场景内物体上组件"的 public 方法,
/// DDOL 的 Manager 绑不到),且希望所有"转换到别的场景"的发起方(刷卡门 / 场景 UI / 内容脚本)
/// 都经同一套公开方法告知 Manager"应该怎么转换"。
///
/// 与门系统的分工:正常关卡推进仍走刷卡门(RequestExitKeyed → T0–T3 门演出,落点由 PortalDoor 负责);
/// 本组件的直达方法跳过钥匙与门演出,只做无演出的场景转换(调试 / 演示 / 选关 / 彩蛋)。
/// 玩家落点与钥匙语义不是本组件的职责 —— 直达后玩家原地不动,需要落点由调用方自理。
///
/// 放置指南(每场景保证一个):
///   - 每个关卡场景:在场景根建一个空物体挂本组件(建议命名 "Scene Transition"),
///     该关 UI(下一关 / 回主菜单 / 选关按钮)的持久 onClick 绑到它的 public 方法;
///   - 菜单 / 结局场景(Room_00 等):可不挂 —— 其 UI 桥(MainMenuUI / PauseMenu / EndingOverlay)
///     已提供按钮入口;要放"直达某关"的调试按钮时再挂一个即可;
///   - Persistance:无需挂(它的 UI 就是场景里的 PauseMenu)。
///
/// 空安全:Manager / 流程缺失(开发者直玩、不在对应状态)时方法记录日志并忽略,绝不抛错。
/// Play Mode only。
/// </summary>
public class SceneTransition : MonoBehaviour
{
    /// <summary>
    /// 回主菜单(经流程:自动存档 → 收局 → 卸载关卡/玩家 → Room_00 背景)。
    /// 只在游玩中有效;其它状态(主菜单 / 结局 / 开发直玩)由流程拒绝并留日志。
    /// </summary>
    public void GoToMainMenu()
    {
        GameFlowManager.QuitToMenu();
    }

    /// <summary>
    /// 直达线性下一关(无门演出;跳过当前关的刷卡钥匙流程)。
    /// 不存在下一关(最后一关 / 当前关不在列表)或非稳定点会被 Manager 拒绝并留日志。
    /// </summary>
    public void GoToNextLevel()
    {
        var ltm = LevelTransitionManager.Instance;
        if (ltm == null)
        {
            LogMissingManager();
            return;
        }
        int next = ltm.CurrentLevelIndex + 1;
        if (next < 0 || next >= ltm.LevelCount)
        {
            Debug.LogWarning($"[SceneTransition] {name}: 当前关(序数 {ltm.CurrentLevelIndex})没有线性下一关，忽略 GoToNextLevel", this);
            return;
        }
        ltm.RequestDirectSwitch(ltm.GetLevelPath(next));
    }

    /// <summary>
    /// 直达关卡注册表第 levelIndex 关(选关 UI / 彩蛋传送 / 调试;无门演出)。
    /// 序号越界、目标非法或非稳定点会被 Manager 拒绝并留日志。
    /// </summary>
    public void GoToLevel(int levelIndex)
    {
        var ltm = LevelTransitionManager.Instance;
        if (ltm == null)
        {
            LogMissingManager();
            return;
        }
        string path = ltm.GetLevelPath(levelIndex);
        if (string.IsNullOrEmpty(path))
        {
            Debug.LogWarning($"[SceneTransition] {name}: 关卡序号 {levelIndex} 越界(共 {ltm.LevelCount} 关)，忽略 GoToLevel", this);
            return;
        }
        ltm.RequestDirectSwitch(path);
    }

    /// <summary>
    /// 经流程触发结局(结局目录校验 / 解锁条件 / 纯色幕或专属场景演出均由 EndingCatalog +
    /// GameFlowManager 负责)。只在游玩中有效;结局 id 未登记会被流程拒绝并留日志。
    /// </summary>
    public void GoToEnding(string endingId)
    {
        GameFlowManager.TriggerEnding(endingId);
    }

    private void LogMissingManager()
    {
        Debug.LogWarning($"[SceneTransition] {name}: 未找到 LevelTransitionManager（开发者直玩?），转换请求忽略", this);
    }
}
