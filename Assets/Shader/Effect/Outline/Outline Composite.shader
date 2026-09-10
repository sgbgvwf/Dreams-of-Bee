// Outline Composite - 描边合成后处理
// 输入: _MaskTex = 描边物体深度遮罩 (RFloat, 线性米制深度)
// 流程: 阈值化 → 膨胀相减 → 加法混合到主画面
// 注: 此 shader 仅用于 CustomPass 的 CoreUtils.DrawFullScreen，不参与 HDRP 相机渲染
Shader "Hidden/Outline Composite"
{
    Properties
    {
        _MaskTex ("Mask", 2D) = "black" {}  // 定义一个2D纹理
        _OutlineColor ("Outline Color", Color) = (1, 0.9, 0.1, 1)   // 描边颜色，默认黄色
        _OutlineWidth ("Outline Width (px)", Int) = 2   // 描边宽度，默认为2像素
        _OutlineAlpha ("Outline Alpha", Range(0, 1)) = 1    // 描边强度，范围为0-1
        _DepthThreshold ("Mask Depth Threshold", Float) = 0.001 // 深度阈值，大于这个值的像素被认为是物体区域
    }
    SubShader   // 子Shader
    {
        Pass    // 渲染通道
        {
            Cull Off    // 背面剔除：关
            ZTest Always    // 深度测试：始终通过，即不会被遮挡
            ZWrite Off  // 深度写入：关，避免影响深度缓冲
            Blend One One   // 混合：源系数为1、目标系数为1，等于加法混合

            CGPROGRAM
            #pragma vertex vert //顶点着色器
            #pragma fragment frag   //片元着色器

            #include "UnityCG.cginc"

            struct v2f
            {
                float2 uv : TEXCOORD0;  // 纹理坐标
                float4 vertex : SV_POSITION;    // 裁剪空间下的顶点位置
            };

            sampler2D _MaskTex;
            float4 _MaskTex_TexelSize;
            float4 _OutlineColor;
            int _OutlineWidth;
            float _OutlineAlpha;
            float _DepthThreshold;

            // 顶点着色器生成全屏三角形，给片元着色器提供遍历
            v2f vert(uint vertexID : SV_VertexID)
            {
                v2f o;
                float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
                o.vertex = float4(uv * 2.0 - 1.0, 0.0, 1.0);    // 将uv映射到裁剪空间
                o.uv = uv;
                return o;
            }

            // 深度遮罩阈值化：深度 > 0 即描边物体区域（线性深度永不为 0）
            // 相机渲染到 RT 的存储方向与全屏绘制的屏幕 UV 相反，采样时翻转 V
            float SampleMask(float2 uv)
            {
                float depth = tex2D(_MaskTex, float2(uv.x, 1.0 - uv.y)).r;
                return depth > _DepthThreshold ? 1.0 : 0.0; // 二值化
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 texel = _MaskTex_TexelSize.xy;   // 获取一个纹素在uv中的大小，用于偏移邻域采样
                float original = SampleMask(i.uv);
                float dilated = 0.0;    // 初始化膨胀结果

                // 8 邻域（按半径）最大采样 = 膨胀
                int r = _OutlineWidth;
                for (int x = -r; x <= r; x++)
                {
                    for (int y = -r; y <= r; y++)   // 双重循环遍历附近8个邻域
                    {
                        dilated = max(dilated, SampleMask(i.uv + float2(x, y) * texel));    // 一旦邻域内任意一个像素为1，则膨胀
                    }
                }

                // 膨胀结果减去原遮罩 = 物体外轮廓
                float outline = saturate(dilated - original);
                return float4(_OutlineColor.rgb * outline * _OutlineAlpha, 1.0);    // 颜色*强度*透明度（其实这里的“透明度”并没有控制透明度）
            }
            ENDCG
        }
    }
}
