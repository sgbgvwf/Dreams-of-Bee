using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// 昼夜天体循环（功能脚本）：让 Sun / Moon 两个天体按固定节奏自动交替 ——
/// 一方全亮时另一方全灭，升落过程平滑过渡，无限循环。
///
/// 每个瞬间产出一对归一化亮度百分比（0..1，此消彼长、和恒为 1），同时驱动三样东西：
///   1. 光源强度   = Awake 基线的光强 × 百分比（HDRP HDAdditionalLightData.intensity）
///   2. 球体自发光 = Awake 基线的 _EmissiveColor × 百分比（HDRP/Lit；只缩放强度，色相不变）
///   3. 球体可见性 = Renderer.enabled（百分比为 0 时隐藏，避免熄灭的天体被别的光照成白球）
/// 基线在 Awake 捕获一次，所以场景里光强 / 材质怎么摆，脚本就按比例把它缩到 0。
/// 颜色本身从不改写，只按百分比整体缩放 —— 与项目里灯面 _Light 的约定一致。
/// 亮度百分比同时写入 SunMoonStateSO 镜像，供雾效 / 后处理 / UI 等别的系统轮询。
///
/// 挂哪：场景里一个常驻物体（脚本自身不发光，挂 Room 根或空物体都行）。
///   建议不要挂在 Sun / Moon 自己身上 —— 本脚本是"导演"，不属于任何一方；
///   挂上去虽然技术上仍能运行（隐藏用的是 Renderer.enabled，不停 GameObject），
///   但会让"谁控制谁"变得难读，且日后若改成停用物体就会失效。
/// 绑什么：sunRoot / moonRoot 拖两个天体的根物体（根上带球体渲染器，光源挂自身或子节点）。
///   两个都必填，缺一个直接报错拒绝运行，不做自动查找。
/// 常见坑：
///   1. 两个天体的 GameObject active 由本脚本接管 —— 进 Play 强制激活，之后靠 Renderer.enabled
///      按亮度显隐。场景里手关的天体（比如 Sun）进 Play 会被点亮。
///   2. 天体根上必须有 Renderer，根或子节点上必须有 Light，且渲染器材质必须是 HDRP/Lit
///      （要有 _EmissiveColor）—— 任一条不满足直接报错，不做替换 / 降级。
///   3. 本脚本独占这两个天体光源的强度与材质自发光，不要再叠加 FlickeringLight /
///      LightColorAlternator 之类的效果脚本。
/// </summary>
public class SunMoonCycle : MonoBehaviour
{
    [Header("天体（拖根物体：根上带球体渲染器，光源挂自身或子节点）")]
    [SerializeField, Tooltip("太阳天体的根物体（场景里的 Sun）。必填。")]
    private GameObject sunRoot;

    [SerializeField, Tooltip("月亮天体的根物体（场景里的 Moon）。必填。")]
    private GameObject moonRoot;

    [Header("节奏（秒）")]
    [SerializeField, Min(0f), Tooltip("太阳保持全亮的时间。")]
    private float sunHoldSeconds = 12f;

    [SerializeField, Min(0f), Tooltip("月亮保持全亮的时间。")]
    private float moonHoldSeconds = 12f;

    [SerializeField, Min(0.01f), Tooltip("一方全亮过渡到另一方全亮的时间（升落过程）。")]
    private float transitionSeconds = 6f;

    [Header("起始")]
    [SerializeField, Tooltip("进入 Play 时从哪一方开始：勾选 = 先太阳全亮，取消 = 先月亮全亮。")]
    private bool startWithSun = true;

    [Header("亮度镜像（供别的系统读取）")]
    [SerializeField, Tooltip("亮度百分比写入的镜像资产（Resources/SunMoon/SunMoonStateSO）。留空则退回同名静态 Instance。")]
    private SunMoonStateSO stateMirror;

    // --- 解析出的引用与 Start 基线 ---
    private HDAdditionalLightData sunHd, moonHd;
    private Renderer sunRenderer, moonRenderer;
    private Material sunMaterial, moonMaterial;   // 运行时实例，不污染材质资产
    private float sunBaseIntensity, moonBaseIntensity;
    private Color sunBaseEmissive, moonBaseEmissive;

    // --- 当前亮度百分比（0..1） ---
    private float sunBrightness = 1f;
    private float moonBrightness;

    private float elapsed;      // 循环内已走过的秒数
    private bool ready;         // 引用 / 基线全部就绪才驱动
    private bool warnedNoMirror;

    private static readonly int EmissiveColorId = Shader.PropertyToID("_EmissiveColor");

    // === 只读访问（同场景脚本直接读组件；跨场景读 SunMoonStateSO 镜像） ===
    /// <summary>太阳当前亮度百分比（0..1）。</summary>
    public float SunBrightness => sunBrightness;

    /// <summary>月亮当前亮度百分比（0..1）。</summary>
    public float MoonBrightness => moonBrightness;

    private SunMoonStateSO Mirror => stateMirror != null ? stateMirror : SunMoonStateSO.Instance;

    private void Awake()
    {
        ready = ResolveAndCapture();
        elapsed = startWithSun ? 0f : sunHoldSeconds + transitionSeconds;   // 从“月亮全亮”切入
    }

    private void Update()
    {
        if (!ready) return;

        elapsed += Time.deltaTime;
        EvaluateBrightness();
        Apply();
    }

    /// <summary>按节奏把 elapsed 折算成一对亮度百分比（梯形曲线：全亮 — 过渡 — 全亮 — 过渡）。</summary>
    private void EvaluateBrightness()
    {
        // transitionSeconds 由 [Min(0.01f)] 保证非零，cycleLength 恒 > 0，无除零分支。
        float cycleLength = sunHoldSeconds + transitionSeconds + moonHoldSeconds + transitionSeconds;
        float t = Mathf.Repeat(elapsed, cycleLength);
        float sunHoldEnd = sunHoldSeconds;
        float sunToMoonEnd = sunHoldEnd + transitionSeconds;
        float moonHoldEnd = sunToMoonEnd + moonHoldSeconds;

        if (t < sunHoldEnd)
        {
            sunBrightness = 1f;
            moonBrightness = 0f;
        }
        else if (t < sunToMoonEnd)
        {
            float k = Smooth01((t - sunHoldEnd) / transitionSeconds);
            sunBrightness = 1f - k;
            moonBrightness = k;
        }
        else if (t < moonHoldEnd)
        {
            sunBrightness = 0f;
            moonBrightness = 1f;
        }
        else
        {
            float k = Smooth01((t - moonHoldEnd) / transitionSeconds);
            sunBrightness = k;
            moonBrightness = 1f - k;
        }
    }

    private void Apply()
    {
        sunHd.intensity = sunBaseIntensity * sunBrightness;
        moonHd.intensity = moonBaseIntensity * moonBrightness;

        sunMaterial.SetColor(EmissiveColorId, ScaleRgb(sunBaseEmissive, sunBrightness));
        moonMaterial.SetColor(EmissiveColorId, ScaleRgb(moonBaseEmissive, moonBrightness));

        // 全灭时隐藏球体：否则不发光的白球会被场景里别的光照亮露馅。
        sunRenderer.enabled = sunBrightness > 0f;
        moonRenderer.enabled = moonBrightness > 0f;

        SunMoonStateSO mirror = Mirror;
        if (mirror != null)
        {
            mirror.Push(sunBrightness, moonBrightness);
        }
        else if (!warnedNoMirror)
        {
            warnedNoMirror = true;
            Debug.LogWarning($"[SunMoonCycle] {name}: 没有可用的 SunMoonStateSO 镜像，" +
                "亮度百分比无法被别的系统读取（循环本身照常运行）。", this);
        }
    }

    /// <summary>
    /// 解析两个天体的光源 / 渲染器并捕获基线。任何一项缺失都直接报错返回 false，
    /// 脚本拒绝运行 —— 不自动查找、不降级。
    /// </summary>
    private bool ResolveAndCapture()
    {
        if (sunRoot == null || moonRoot == null)
        {
            Debug.LogError($"[SunMoonCycle] {name}: sunRoot / moonRoot 没有绑定。" +
                "请在 Inspector 里把 Sun 和 Moon 的根物体分别拖上。", this);
            return false;
        }

        // 天体 active 由本脚本接管：进 Play 一律激活，显隐交给 Renderer.enabled。
        if (!sunRoot.activeSelf) sunRoot.SetActive(true);
        if (!moonRoot.activeSelf) moonRoot.SetActive(true);

        sunRenderer = sunRoot.GetComponent<Renderer>();
        moonRenderer = moonRoot.GetComponent<Renderer>();
        if (sunRenderer == null || moonRenderer == null)
        {
            Debug.LogError($"[SunMoonCycle] {name}: 天体根物体上找不到 Renderer" +
                $"（Sun={(sunRenderer == null ? "缺" : "有")}，Moon={(moonRenderer == null ? "缺" : "有")}）。" +
                "请把球体渲染器所在的物体拖为根。", this);
            return false;
        }

        Light sunLight = sunRoot.GetComponentInChildren<Light>(true);
        Light moonLight = moonRoot.GetComponentInChildren<Light>(true);
        if (sunLight == null || moonLight == null)
        {
            Debug.LogError($"[SunMoonCycle] {name}: 天体根物体自身或子节点上找不到 Light" +
                $"（Sun={(sunLight == null ? "缺" : "有")}，Moon={(moonLight == null ? "缺" : "有")}）。", this);
            return false;
        }

        sunHd = sunLight.GetComponent<HDAdditionalLightData>();
        moonHd = moonLight.GetComponent<HDAdditionalLightData>();
        if (sunHd == null || moonHd == null)
        {
            Debug.LogError($"[SunMoonCycle] {name}: 天体光源上没有 HDAdditionalLightData（不是 HDRP 光源？）。", this);
            return false;
        }

        // 先查共享资产再取运行时实例：避免材质不合格时白造一份副本、也避免
        // Unity 因 material 访问在编辑器里刷实例化日志。
        Material sunShared = sunRenderer.sharedMaterial;
        Material moonShared = moonRenderer.sharedMaterial;
        if (sunShared == null || moonShared == null ||
            !sunShared.HasProperty(EmissiveColorId) || !moonShared.HasProperty(EmissiveColorId))
        {
            Debug.LogError($"[SunMoonCycle] {name}: 天体材质缺少 _EmissiveColor" +
                $"（Sun={(sunShared == null ? "无材质" : sunShared.name)}，" +
                $"Moon={(moonShared == null ? "无材质" : moonShared.name)}）。本脚本要求 HDRP/Lit 材质。", this);
            return false;
        }

        // 运行时实例：改的是这两个渲染器的副本，不影响共用同一材质资产的其他物体。
        sunMaterial = sunRenderer.material;
        moonMaterial = moonRenderer.material;

        sunBaseIntensity = sunHd.intensity;
        moonBaseIntensity = moonHd.intensity;
        sunBaseEmissive = sunMaterial.GetColor(EmissiveColorId);
        moonBaseEmissive = moonMaterial.GetColor(EmissiveColorId);

        // 强度由本脚本接管，光源组件本身保持开启（亮度 0 时强度为 0，等于不发光）。
        sunLight.enabled = true;
        moonLight.enabled = true;

        return true;
    }

    /// <summary>平滑插值 0..1（smoothstep），用于升落过渡。</summary>
    private static float Smooth01(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    /// <summary>只缩放 RGB、保留 alpha —— 自发光亮度整体按百分比缩，色相不动。</summary>
    private static Color ScaleRgb(Color c, float k)
    {
        return new Color(c.r * k, c.g * k, c.b * k, c.a);
    }
}
