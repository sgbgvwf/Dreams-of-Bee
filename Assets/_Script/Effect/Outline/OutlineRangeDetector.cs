using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Outline 层管理（描边触发由交互射线负责，本脚本不自己发射线）。
///
/// 交互射线（BeeInteractionController 每帧瞄准检测）命中带 Interactable 的物体时，
/// 调用 AddOutline 把其渲染器切至 Outline 层 —— 遮罩相机按该层渲染出描边；
/// 准星移开 / 超出射程时调用 RemoveOutline 恢复原层。
///
/// 场景手动配置：
///  - 相机同步：主相机上的 CameraSync，把视角同步给遮罩相机
///  - 合成：Volume 上的 Custom Pass Volume → 挂 OutlineCompositePass
/// </summary>
public class OutlineRangeDetector : MonoBehaviour
{
    public static OutlineRangeDetector Instance { get; private set; }

    private int outlineLayer = -1;

    /// <summary>描边中的渲染器 → 原层</summary>
    private readonly Dictionary<Renderer, int> outlineRenderers = new Dictionary<Renderer, int>();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        outlineLayer = LayerMask.NameToLayer("Outline");
        if (outlineLayer < 0)
            Debug.LogError("OutlineRangeDetector: 找不到 'Outline' 层，请在 Project Settings > Tags and Layers 中添加");
    }

    private void OnDestroy()
    {
        // 恢复所有描边物体的原层
        foreach (var pair in outlineRenderers)
        {
            if (pair.Key != null)
                pair.Key.gameObject.layer = pair.Value;
        }
        outlineRenderers.Clear();

        if (Instance == this)
            Instance = null;
    }

    /// <summary>把目标物体加入描边集合（渲染器切换至 Outline 层）</summary>
    public void AddOutline(GameObject target)
    {
        if (target == null || outlineLayer < 0)
            return;

        foreach (var r in target.GetComponentsInChildren<Renderer>())
        {
            if (r == null || outlineRenderers.ContainsKey(r))
                continue;
            outlineRenderers.Add(r, r.gameObject.layer);
            r.gameObject.layer = outlineLayer;
        }
    }

    /// <summary>把目标物体移出描边集合（恢复原层）</summary>
    public void RemoveOutline(GameObject target)
    {
        if (target == null)
            return;

        foreach (var r in target.GetComponentsInChildren<Renderer>())
        {
            if (r == null)
                continue;
            if (outlineRenderers.TryGetValue(r, out int originalLayer))
            {
                r.gameObject.layer = originalLayer;
                outlineRenderers.Remove(r);
            }
        }
    }
}
