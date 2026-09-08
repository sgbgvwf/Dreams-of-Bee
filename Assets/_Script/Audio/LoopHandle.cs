using UnityEngine;

/// <summary>
/// 循环音效句柄:AudioManager.StartLoop 返回的引用,Stop() 停止并销毁播放对象。
/// 挂在循环音效的 AudioSource 所在 GameObject 上:
///   2D 全局循环(落风)→ 挂在 AudioManager 下,跨场景存活;
///   3D 跟随循环(滑门/灯嗡鸣)→ 挂在音源物体下,随场景卸载自动销毁。
/// Stop 与 OnDestroy 全路径幂等、销毁安全。
/// </summary>
public class LoopHandle : MonoBehaviour
{
    private bool stopped;

    public void Stop()
    {
        if (this == null || stopped) return;   // this == null 覆盖场景卸载后对象已销毁的情况
        stopped = true;
        AudioManager.Instance?.UnregisterLoop(this);
        Destroy(gameObject);
    }

    private void OnDestroy()
    {
        // 父物体(音源)随场景卸载销毁时,同步从管理器注销,避免悬挂引用
        if (!stopped && AudioManager.Instance != null)
            AudioManager.Instance.UnregisterLoop(this);
    }
}
