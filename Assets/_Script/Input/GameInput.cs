using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 输入动作唯一入口(静态门面,仿 GameEvents / PlayerStateSO.Instance 惯例):
/// 输入资产在 Resources 下按路径懒加载,动作只在加载点解析一次并缓存,
/// 之后以强类型属性暴露。消费脚本一律经这里取动作 —— 不再各自拖 InputActionAsset、
/// 不再 FindAction("字符串");动作改名 / 重绑只动资产与这一个文件。
///
/// 启用策略:首次访问即加载资产并启用动作地图(全项目仅一张 "Keyboard and Mouse"),
/// 此后地图常驻启用 —— 谁在消费、何时响应由各消费脚本自己的订阅与门控决定
/// (PauseMenu.IsPaused / FlowStateSO.PauseAllowed / BeeFlightController.InputLocked…),
/// 不受玩家场景装卸影响;主菜单里 Esc 照常送达,由 PauseInput 按状态门控忽略。
///
/// 失败策略:资产缺失 / 缺地图 / 缺动作 = 配置错误,报一次错并让对应动作返回 null,
/// 不兜底 —— 消费方原有的空判让"该输入不可用"显式可见,错误不会被静默吞掉。
/// </summary>
public static class GameInput
{
    /// <summary>资产在 Assets/Resources/ 下的路径(不含扩展名):GUI 里把 Input Action.inputactions 放到 Resources/InputAction/ 下。</summary>
    private const string AssetPath = "InputAction/Input Action";
    private const string MapName = "Keyboard and Mouse";

    private static InputActionAsset asset;
    private static InputAction fly;
    private static InputAction look;
    private static InputAction takeOff;
    private static InputAction interact;
    private static InputAction pause;
    private static bool loadAttempted;   // 加载只尝试一次:失败只报一次错,不每帧刷屏

    public static InputAction Fly => EnsureLoaded() ? fly : null;
    public static InputAction Look => EnsureLoaded() ? look : null;
    public static InputAction TakeOff => EnsureLoaded() ? takeOff : null;
    public static InputAction Interact => EnsureLoaded() ? interact : null;
    public static InputAction Pause => EnsureLoaded() ? pause : null;

    private static bool EnsureLoaded()
    {
        if (asset != null) return true;
        if (loadAttempted) return false;
        loadAttempted = true;

        asset = Resources.Load<InputActionAsset>(AssetPath);
        if (asset == null)
        {
            Debug.LogError($"[GameInput] 找不到输入资产 Resources/{AssetPath}.inputactions —— 输入全部不可用。请把 Input Action.inputactions 移到 Assets/Resources/InputAction/ 下。");
            return false;
        }

        var map = asset.FindActionMap(MapName);
        if (map == null)
        {
            Debug.LogError($"[GameInput] 输入资产里没有动作地图 '{MapName}' —— 输入全部不可用。");
            asset = null;
            return false;
        }

        fly = map.FindAction("Fly");
        look = map.FindAction("Look");
        takeOff = map.FindAction("TakeOff");
        interact = map.FindAction("Interact");
        pause = map.FindAction("Pause");
        if (fly == null || look == null || takeOff == null || interact == null || pause == null)
            Debug.LogError("[GameInput] 输入资产缺少动作 Fly / Look / TakeOff / Interact / Pause(缺失者不可用,消费方按无该输入处理)。");

        map.Enable();   // 地图常驻启用:动作照常送达,响应与否由消费方门控(见类注释)
        return true;
    }
}
