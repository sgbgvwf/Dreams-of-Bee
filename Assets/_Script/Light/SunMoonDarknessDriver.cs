using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 昼夜暗区驱动（功能脚本）：按昼夜暗度驱动 MeltGlass 那栋玻璃 —— 材质与碰撞体。
/// SunMoonCycle 只管把昼夜亮度写进镜像，本脚本是它的消费方。
///
/// 开关：整套由柴油机（GeneratorStateSO.Started）门控。**未启动 = 本脚本完全不工作** ——
/// 不驱动材质、不碰碰撞体，两者都保持作者在编辑器里摆的样子。柴油机单向（启动即退役、
/// 熄不了火），所以启动后不会再回到停摆态。
/// 注意昼夜交替本身【不受】柴油机影响 —— 那个由 SunMoonCycle 一直在跑，不在本脚本。
///
/// 判据：太阳和月亮亮度【都】低于阈值 threshold 时进入"暗区"—— 每次升落过渡的中间段。
/// 因为两方亮度恒满足 太阳 + 月亮 = 1，"都低于阈值"等价于"较亮的一方也低于阈值"，
/// 所以只比较 max(sun, moon)。由此阈值必须 &gt; 0.5：若 ≤ 0.5 则该条件永远不可能成立。
///
/// 暗度百分比 k（0..1）：阈值边界处为 0 —— 正中间最暗处为 1（此时两方各 0.5）。
///   k = 0    两方至少一方够亮（不在暗区）
///   k = 1    两方亮度相等、各 0.5 的最暗瞬间
///
/// 按同一个 k 驱动那栋玻璃的两样东西，所以"Melt 开始变化"与"碰撞体关闭"必然同瞬间：
///   1. 材质浮点值 = Lerp(valueWhenBright, valueWhenDark, k)，线性插值、连续驱动
///   2. 碰撞体     = 进暗区（k &gt; 0）关闭，出暗区恢复
///
/// 曲线形状（以 Room_04 的 _Melt 为例，亮区端点 0.5、暗区端点 0）：
/// 每次升落过渡中，值先从亮区端点下降、到【两方亮度相等各 0.5】的瞬间触底，
/// 再对称地升回亮区端点。两个方向的变化关于中点镜像 —— 因为 k 只取决于
/// max(sun, moon)，而它在过渡中先降后升、关于中点对称。
/// 亮区（任一方全亮及两侧保持段）里值恒为亮区端点。
///
/// 读的是 SunMoonStateSO 镜像，所以放在哪个场景都行（不依赖 SunMoonCycle 是否已加载）。
///
/// 挂哪：任意常驻物体。Play Mode only。
/// 绑什么：generatorState 拖柴油机的 GeneratorStateSO 资产（留空则退回静态 Instance）；
///   targetMaterial 拖要改的材质资产（.mat），propertyName 填属性名（浮点属性），
///   meltGlassCollider 拖那栋玻璃的碰撞体（留空则只驱动材质、不管碰撞体）。
///   阈值 / 两个端点值都在 Inspector 调。
/// 常见坑：
///   1. 改的是材质资产本身 —— 所有用到这个材质的物体都会一起变。想让某个物体独立，
///      给它单独复制一份 .mat。
///   2. 本脚本独占这个材质属性，不要再让别的脚本 / 动画去写同一个属性，否则互相打架。
///   3. 运行期改动只在内存、不写回磁盘（退出 Play 后 Unity 还原资产），调试时别以为改坏了。
///   4. 除柴油机之外还有一道"读数未就绪"保护：镜像 HasData 为 false（场景里根本没有
///      SunMoonCycle）时也不驱动，并警告一次 —— 否则会把材质一直按"最暗"处理。
/// </summary>
public class SunMoonDarknessDriver : MonoBehaviour
{
    [Header("开关（柴油机）")]
    [SerializeField, Tooltip("柴油机状态镜像（Resources/Generator/GeneratorStateSO）。柴油机启动后本脚本才工作；留空则退回静态 Instance")]
    private GeneratorStateSO generatorState;

    [Header("暗区阈值（必须 > 0.5）")]
    [SerializeField, Range(0.5f, 1f), Tooltip("太阳和月亮亮度都低于它时进入暗区。必须 > 0.5 —— 两方亮度和恒为 1，都低于 0.5 不可能发生（0.5 即永不触发）")]
    private float threshold = 0.85f;

    [Header("目标材质（拖 .mat 资产）")]
    [SerializeField, Tooltip("要驱动的材质资产。必填 —— 注意它可能被多个物体共用，改动是全局的。")]
    private Material targetMaterial;

    [SerializeField, Tooltip("要改的材质属性名，必须用着色器里的【引用名】而非显示名 —— Shader Graph 里显示名是 Melt，引用名要填 _Melt。必须是浮点属性。必填。")]
    private string propertyName = "_Melt";

    [Header("取值")]
    [SerializeField, Tooltip("不在暗区时的取值（暗度 0%）。")]
    private float valueWhenBright = 0.5f;

    [SerializeField, Tooltip("暗区最深处的取值（暗度 100%，即两方亮度相等各 0.5 的瞬间）。")]
    private float valueWhenDark;

    [Header("碰撞体（进暗区即关闭，出暗区恢复）")]
    [SerializeField, Tooltip("MeltGlass 那栋玻璃的碰撞体 —— 与上面材质联动的那个物体。留空则不管碰撞体，只驱动材质")]
    private Collider meltGlassCollider;

    [Header("亮度镜像")]
    [SerializeField, Tooltip("亮度来源镜像（Resources/SunMoon/SunMoonStateSO）。留空则退回同名静态 Instance。")]
    private SunMoonStateSO stateMirror;

    // --- 解析结果 ---
    private int propertyId;
    private bool ready;

    // --- 当前暗度百分比（0 = 不在暗区，1 = 最暗） ---
    private float darknessPercent;

    private bool warnedNoMirror;
    private bool warnedNoData;

    /// <summary>当前暗度百分比（0..1）：0 = 不在暗区，1 = 两方各半的最暗处。调试用。</summary>
    public float DarknessPercent => darknessPercent;

    private SunMoonStateSO Mirror => stateMirror != null ? stateMirror : SunMoonStateSO.Instance;

    private void Awake()
    {
        // 只校验，不动任何东西：柴油机没启动前，材质与碰撞体都保持作者摆的样子。
        ready = Validate();
    }

    private void Update()
    {
        if (!ready) return;

        if (!IsGeneratorStarted())
        {
            // 柴油机未启动：这套（Melt 材质 + 玻璃碰撞体）整套停摆 —— 不驱动、不写任何状态。
            // 柴油机单向（启动即退役、熄不了火），所以启动后不会再回到这里。
            // 注意昼夜交替本身不受影响，那个由 SunMoonCycle 跑，与柴油机无关。
            return;
        }

        SunMoonStateSO mirror = Mirror;
        if (mirror == null)
        {
            if (!warnedNoMirror)
            {
                warnedNoMirror = true;
                Debug.LogWarning($"[SunMoonDarknessDriver] {name}: 没有可用的 SunMoonStateSO 镜像，" +
                    "无法获知昼夜亮度，材质不会被驱动。", this);
            }
            return;
        }

        if (!mirror.HasData)
        {
            // 镜像还是资产默认的 0/0。直接当"最暗"处理会得到一个假的满值，所以在此之前
            // 不驱动，并提示一次。（柴油机没启动的情况上面已经拦掉了，走到这里说明
            // 场景里根本没有 SunMoonCycle 在写这个镜像。）
            if (!warnedNoData)
            {
                warnedNoData = true;
                Debug.LogWarning($"[SunMoonDarknessDriver] {name}: SunMoonStateSO 还没有被写入过" +
                    "（场景里没有 SunMoonCycle？），材质与碰撞体保持不动。", this);
            }
            return;
        }

        // 两方都低于阈值 ⟺ 较亮的一方也低于阈值。max 恒 ≥ 0.5，所以阈值必须 > 0.5。
        float brighter = Mathf.Max(mirror.SunBrightness, mirror.MoonBrightness);
        darknessPercent = brighter < threshold
            ? Mathf.InverseLerp(threshold, 0.5f, brighter)   // 阈值边界 0 → 正中间最暗 1
            : 0f;

        Apply();
    }

    /// <summary>
    /// 柴油机是否已启动。拖引用优先，留空退回静态 Instance —— 与 SunMoonCycle 那边同一套取用方式。
    /// 资产缺失时 Instance 只报错一次并返回 null，此处按"未启动"处理（不驱动）。
    /// </summary>
    private bool IsGeneratorStarted()
    {
        GeneratorStateSO g = generatorState != null ? generatorState : GeneratorStateSO.Instance;
        return g != null && g.Started;
    }

    /// <summary>
    /// 把当前暗度施加到材质与碰撞体上（本脚本唯一的输出口）。
    /// 两者都用同一个 darknessPercent，所以"Melt 开始变化"与"碰撞体关闭"必然同瞬间。
    /// </summary>
    private void Apply()
    {
        targetMaterial.SetFloat(propertyId, Mathf.Lerp(valueWhenBright, valueWhenDark, darknessPercent));

        // 碰撞体跟随的是【两个天体的亮度关系】（暗区），不是玻璃自身的什么状态：
        // 进暗区（= Melt 开始变化的同一瞬间）关闭，出暗区恢复。
        if (meltGlassCollider != null)
            meltGlassCollider.enabled = darknessPercent <= 0f;
    }

    /// <summary>
    /// 校验材质与属性名。任一项不合格直接报错返回 false，脚本拒绝运行 —— 不自动查找、不降级。
    /// </summary>
    private bool Validate()
    {
        if (targetMaterial == null)
        {
            Debug.LogError($"[SunMoonDarknessDriver] {name}: targetMaterial 没有绑定。" +
                "请在 Inspector 里拖入要驱动的材质资产（.mat）。", this);
            return false;
        }

        if (string.IsNullOrWhiteSpace(propertyName))
        {
            Debug.LogError($"[SunMoonDarknessDriver] {name}: propertyName 是空的，不知道要改哪个属性。", this);
            return false;
        }

        int index = targetMaterial.shader.FindPropertyIndex(propertyName);
        if (index < 0)
        {
            Debug.LogError($"[SunMoonDarknessDriver] {name}: 材质 {targetMaterial.name} 的着色器上" +
                $"没有属性 \"{propertyName}\"。请核对属性名。", this);
            return false;
        }

        ShaderPropertyType type = targetMaterial.shader.GetPropertyType(index);
        if (type != ShaderPropertyType.Float && type != ShaderPropertyType.Range)
        {
            Debug.LogError($"[SunMoonDarknessDriver] {name}: 属性 \"{propertyName}\" 在 {targetMaterial.name} 上是" +
                $"{type} 类型，不是浮点。本脚本只能驱动 Float / Range 属性。", this);
            return false;
        }

        propertyId = Shader.PropertyToID(propertyName);

        // 滑条属性的声明范围只在 Inspector 里约束，运行期 SetFloat 不会自动夹紧。
        // 端点值写超了不会报错、只会得到意想不到的画面，所以在这里明确喊一声。
        if (type == ShaderPropertyType.Range)
        {
            Vector2 limits = targetMaterial.shader.GetPropertyRangeLimits(index);
            if (valueWhenBright < limits.x || valueWhenBright > limits.y ||
                valueWhenDark < limits.x || valueWhenDark > limits.y)
            {
                Debug.LogWarning($"[SunMoonDarknessDriver] {name}: 端点值超出属性 \"{propertyName}\" 的声明范围 " +
                    $"[{limits.x}, {limits.y}]（valueWhenBright={valueWhenBright}，valueWhenDark={valueWhenDark}）。" +
                    "运行期不会被夹紧，请确认这是你要的。", this);
            }
        }

        return true;
    }
}
