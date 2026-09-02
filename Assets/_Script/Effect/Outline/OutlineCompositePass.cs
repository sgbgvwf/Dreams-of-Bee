using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// HDRP CustomPass：描边核心。
/// 步骤：
///   1. 用遮罩相机（仅作投影源）把描边物体的线性深度渲染进 MaskRT
///   2. 全屏合成：阈值化 → 膨胀相减得外轮廓 → 加法混合到主画面
/// 挂在 CustomPassVolume 上，injectionPoint 建议 AfterPostProcess。
/// 遮罩相机在 Player prefab 内（玩家是独立常驻场景）：outlineCamera 在 Inspector 直接绑定
/// Player.prefab 内部的 Outline Camera（prefab 资产引用），玩家场景实例化后 Unity 自动把
/// 该引用重写到实际实例上，跨场景成立。
/// </summary>
public class OutlineCompositePass : CustomPass
{
    [Header("数据源")]
    public Camera outlineCamera;          // 遮罩相机（只提供视角，不参与 HDRP 自动渲染）
    public LayerMask outlineLayerMask;    // 描边物体所在层

    [Header("遮罩 RT")]
    public RenderTexture maskRT;          // 运行时按屏幕分辨率自动创建/重建

    [Header("描边参数")]
    public Material compositeMaterial;    // 合成材质（Hidden/Outline Composite）
    public float outlineWidth = 2f;       // 轮廓宽度（像素）
    public Color outlineColor = new Color(1f, 0.9f, 0.1f);
    public float outlineAlpha = 1f;

    const int k_MaskDepthBits = 24;

    protected override void Execute(CustomPassContext ctx)
    {
        if (outlineCamera == null || outlineLayerMask == 0)
            return;

        // 分辨率变化时重建遮罩 RT
        var target = ctx.cameraColorBuffer.rt;
        if (maskRT == null || maskRT.width != target.width || maskRT.height != target.height)
        {
            ReleaseMaskRT();
            maskRT = new RenderTexture(target.width, target.height, k_MaskDepthBits, RenderTextureFormat.RFloat)
            {
                name = "Outline Mask RT",
                filterMode = FilterMode.Point,   // Point 采样避免边缘发虚
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false
            };
        }

        // 1. 渲染描边物体深度到遮罩 RT（R 通道 = 线性米制深度）
        //    maskRT 带深度缓冲，描边物体之间互相遮挡正确
        CustomPassUtils.RenderDepthFromCamera(ctx, outlineCamera, maskRT,
            ClearFlag.All, outlineLayerMask, CustomPass.RenderQueueType.AllOpaque);

        // 2. 合成：膨胀相减得轮廓，加法叠加
        if (compositeMaterial != null)
        {
            compositeMaterial.SetTexture("_MaskTex", maskRT);
            compositeMaterial.SetInt("_OutlineWidth", Mathf.RoundToInt(outlineWidth));
            compositeMaterial.SetColor("_OutlineColor", outlineColor);
            compositeMaterial.SetFloat("_OutlineAlpha", outlineAlpha);
            HDUtils.DrawFullScreen(ctx.cmd, compositeMaterial, ctx.cameraColorBuffer);
        }
    }

    protected override void Cleanup()
    {
        ReleaseMaskRT();
    }

    void ReleaseMaskRT()
    {
        if (maskRT != null)
        {
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(maskRT);
            else
                UnityEngine.Object.DestroyImmediate(maskRT);
            maskRT = null;
        }
    }
}
