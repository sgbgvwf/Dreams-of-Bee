// Outline Composite - 描边合成后处理
// 输入: _MaskTex = 描边物体深度遮罩 (RFloat, 线性米制深度)
// 流程: 阈值化 → 膨胀相减 → 加法混合到主画面
// 注: 此 shader 仅用于 CustomPass 的 CoreUtils.DrawFullScreen，不参与 HDRP 相机渲染
Shader "Hidden/Outline Composite"
{
    Properties
    {
        _MaskTex ("Mask", 2D) = "black" {}
        _OutlineColor ("Outline Color", Color) = (1, 0.9, 0.1, 1)
        _OutlineWidth ("Outline Width (px)", Int) = 2
        _OutlineAlpha ("Outline Alpha", Range(0, 1)) = 1
        _DepthThreshold ("Mask Depth Threshold", Float) = 0.001
    }
    SubShader
    {
        Pass
        {
            Cull Off
            ZTest Always
            ZWrite Off
            // 加法混合：直接叠加到主画面，无需读回主画面颜色
            Blend One One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MaskTex;
            float4 _MaskTex_TexelSize;
            float4 _OutlineColor;
            int _OutlineWidth;
            float _OutlineAlpha;
            float _DepthThreshold;

            // DrawProcedural 全屏三角形（无顶点缓冲，由 SV_VertexID 生成）
            v2f vert(uint vertexID : SV_VertexID)
            {
                v2f o;
                float2 uv = float2((vertexID << 1) & 2, vertexID & 2);
                o.vertex = float4(uv * 2.0 - 1.0, 0.0, 1.0);
                o.uv = uv;
                return o;
            }

            // 深度遮罩阈值化：深度 > 0 即描边物体区域（线性深度永不为 0）
            // 相机渲染到 RT 的存储方向与全屏绘制的屏幕 UV 相反，采样时翻转 V
            float SampleMask(float2 uv)
            {
                float depth = tex2D(_MaskTex, float2(uv.x, 1.0 - uv.y)).r;
                return depth > _DepthThreshold ? 1.0 : 0.0;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 texel = _MaskTex_TexelSize.xy;
                float original = SampleMask(i.uv);
                float dilated = 0.0;

                // 8 邻域（按半径）最大采样 = 膨胀
                int r = _OutlineWidth;
                for (int x = -r; x <= r; x++)
                {
                    for (int y = -r; y <= r; y++)
                    {
                        dilated = max(dilated, SampleMask(i.uv + float2(x, y) * texel));
                    }
                }

                // 膨胀结果减去原遮罩 = 物体外轮廓
                float outline = saturate(dilated - original);
                return float4(_OutlineColor.rgb * outline * _OutlineAlpha, 1.0);
            }
            ENDCG
        }
    }
}
