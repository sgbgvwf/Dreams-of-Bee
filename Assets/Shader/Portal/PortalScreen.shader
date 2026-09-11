// ============================================================================
// 门面屏幕空间 UV —— 传送门"窗户"材质（HDRP 无光照）
//
// 是什么：把一张 RenderTexture（门后相机的 Target Texture）按【当前像素在玩家屏幕上的位置】
//   采样，于是门洞里看到的是"透过这扇门看到的目标关"，而不是贴在门板上的一张动态贴画 ——
//   玩家绕门走动 / 上下飞时会有正确的视差（近处物体比远处动得多）。
//
// 放哪：门面平面（门洞处、门板背后那张 Quad）用的材质上。项目里 = Assets/Resource/Texture/DoorTex.mat，
//   由 Assets/Prefab/移动门.prefab 里的 Quad 引用（7 个场景实例共用这一份材质）。
//
// 绑什么：材质上只需要 _UnlitColorMap = 门后相机渲染的那张 RT（属性名与 HDRP/Unlit 保持一致，
//   所以从 HDRP/Unlit 换到本 shader 时 RT 引用与平铺参数原样保留，不用重拖）。
//
// 为什么屏幕 UV 就够（原理）：
//   门后相机由 PortalViewSync 摆成"玩家位姿经两门相对变换"的位置与朝向，于是门面上一点 X
//   在门后相机裁剪空间里的位置【恒等于】它在玩家相机屏幕上的位置。取自己的屏幕坐标去 RT 的
//   同一位置采样，取到的正好就是"穿过门洞的那条视线"该看到的东西 —— 全程不需要传任何矩阵。
//
// 常见坑：
//   1. 门后相机的 FOV / 宽高比必须与玩家相机一致（PortalViewSync 每帧同步，手改相机会被覆盖）。
//      投影不一致 = 门面画面整体错位；一致还顺带保证 uv 恒在 [0,1] 内，贴近门洞 / 斜视时不拉花。
//   2. _UnlitColorMap 必须指向门后相机的 Target Texture，否则门面是一张死图。
//   3. 门后相机停渲染（总线停写 = 穿门已结算）时 RT 冻结，门面定格属正常现象。
//   4. uv.y 必须翻转（实测 D3D11 + HDRP 下如此）：相机渲染到 RenderTexture 时投影 Y 被 Unity
//      翻转过，采样时要翻回来 —— 漏了这行图像就上下颠倒。
//   5. 本 shader 不做大气散射 / 体积雾（HDRP 的 EvaluateAtmosphericScattering 那一套）——
//      本项目 VisualEnvironment 的 fogType = None，暂时没影响；哪天关卡开了雾再补。
// ============================================================================
Shader "Dreams of Bee/Portal Screen"
{
    Properties
    {
        [MainColor] _UnlitColor("Color", Color) = (1, 1, 1, 1)
        [MainTexture] _UnlitColorMap("Portal RT", 2D) = "black" {}
    }

    // 三个 Pass 共用：include、材质常量缓冲、顶点结构与顶点着色器
    HLSLINCLUDE

    #pragma target 4.5

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
    // 必须排在 ShaderVariables 之后:SpaceTransforms 的 GetObjectToWorldMatrix 直接用 UNITY_MATRIX_M,
    // 而该宏由 HDRP 在 ShaderVariables 里引的 UnityInstancing.hlsl 才定义 —— 提前引会报
    // 'undeclared identifier UNITY_MATRIX_M'。
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/SpaceTransforms.hlsl"

    TEXTURE2D(_UnlitColorMap);
    SAMPLER(sampler_UnlitColorMap);

    CBUFFER_START(UnityPerMaterial)
    float4 _UnlitColor;
    float4 _UnlitColorMap_ST;
    CBUFFER_END

    struct Attributes
    {
        float4 positionOS : POSITION;
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float4 screenPos  : TEXCOORD0;
    };

    Varyings Vert(Attributes input)
    {
        Varyings output;
        float4 posCS = TransformObjectToHClip(input.positionOS.xyz);
        output.positionCS = posCS;
        // ComputeScreenPos 的标准形态：插值 (ndc*0.5+0.5)*w 与 zw，片元里再做透视除法
        output.screenPos.xy = posCS.xy * 0.5 + 0.5 * posCS.w;
        output.screenPos.zw = posCS.zw;
        return output;
    }

    ENDHLSL

    SubShader
    {
        Tags{ "RenderPipeline" = "HDRenderPipeline" "RenderType" = "HDUnlitShader" }

        // ---------- 主渲染：把 RT 按屏幕 UV 采出来写进颜色缓冲（无光照） ----------
        Pass
        {
            Name "ForwardOnly"
            Tags{ "LightMode" = "ForwardOnly" }

            Cull Back
            ZWrite On
            ZTest LEqual

            // 与 HDRP/Unlit 一致：向"延迟/SSS"标记位写 0，声明本像素是前向无光照
            Stencil
            {
                WriteMask 6
                Ref 0
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag(Varyings input) : SV_Target
            {
                float2 uv = input.screenPos.xy / input.screenPos.w;
                // 相机渲染到贴图时投影被翻转，采样时翻回来（见头注释坑 4）
                uv.y = 1.0 - uv.y;

                float3 color = SAMPLE_TEXTURE2D(_UnlitColorMap, sampler_UnlitColorMap, uv).rgb * _UnlitColor.rgb;
                // 与 HDRP/Unlit 相同的曝光约定（相机叠加合成时才不为 1，通常就是 1）
                color *= _DeExposureMultiplier;

                return float4(color, 1.0);
            }
            ENDHLSL
        }

        // ---------- 深度预通道：门面要进 HDRP 的 depth prepass（SSAO / SSR / 接触阴影都读它） ----------
        Pass
        {
            Name "DepthForwardOnly"
            Tags{ "LightMode" = "DepthForwardOnly" }

            Cull Back
            ZWrite On

            // 与 HDRP/Unlit 深度预通道一致
            Stencil
            {
                WriteMask 9
                Ref 1
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            // MSAA 下 HDRP 靠这个变体把深度导出到颜色缓冲（对应 ShaderPassDepthOnly.hlsl 的做法）
            #pragma multi_compile_fragment _ WRITE_MSAA_DEPTH

            #ifdef WRITE_MSAA_DEPTH
            void Frag(Varyings input, out float4 depthColor : SV_Target0)
            {
                // 裁剪空间 z 广播到 rgb + alpha（本材质无 alpha test，恒为 1）
                depthColor = float4(input.positionCS.z, input.positionCS.z, input.positionCS.z, 1.0);
            }
            #else
            void Frag(Varyings input) {}
            #endif
            ENDHLSL
        }

        // ---------- 阴影投射：门面挡光与门板一致（缺这个 pass 门洞处会漏光） ----------
        Pass
        {
            Name "ShadowCaster"
            Tags{ "LightMode" = "ShadowCaster" }

            Cull Back
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            void Frag(Varyings input) {}
            ENDHLSL
        }
    }
}
