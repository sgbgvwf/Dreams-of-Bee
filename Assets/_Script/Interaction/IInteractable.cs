/// <summary>
/// 交互行为接口：由具体功能脚本实现，交互框架只负责调用。
/// 交互判定：命中物体有 Interactable → 拾取；有 IInteractable → 调用 OnInteract()。
/// 具体交互逻辑（拨开关、开门等）全部由实现方自行处理，交互脚本不感知。
/// </summary>
public interface IInteractable
{
    void OnInteract();
}
