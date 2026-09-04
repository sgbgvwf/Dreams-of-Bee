using UnityEngine;

/// <summary>
/// 台灯开关控制器（功能脚本）：实现 IInteractable 接入交互框架，交互框架只负责调用。
/// 挂在开关节点（industrial_pipe_lamp_switch）上：
///  - 自身 transform 即拨杆枢轴：交互时拨杆绕自身局部轴摆动 switchAngle（关 = 左 -角度，开 = 右 +角度，相对基线）
///  - 灯泡亮灭 = 控制台灯下的真实光源（Light 组件，FBX 本身无 Light），开灯全亮 / 关灯全灭
/// 旋转永远从 Awake 基线重算，切换任意次都不漂移。
/// 注意：进入 Play 时的开关姿态即 0° 基线（建模原始姿态一般为居中），脚本启动后按 Start On 摆到对应位置。
/// </summary>
public class LampSwitchController : MonoBehaviour, IInteractable, ISceneSaveable
{
    public enum RotAxis { X, Y, Z }

    [SerializeField, Tooltip("拨杆绕自身哪个局部轴摆动（模型轴向未知，进 Play 试）。")]
    private RotAxis rotationAxis = RotAxis.Y;

    [SerializeField, Tooltip("开/关相对基线的偏转角：关 = -角度（左），开 = +角度（右）。")]
    private float switchAngle = 30f;

    [SerializeField, Tooltip("铰点修正：拨杆节点原点若不在铰点（杆绕中心转而非绕根部拨动），填铰点相对节点原点的偏移。")]
    private Vector3 pivotOffset;

    [SerializeField, Tooltip("真实光源：场景里给灯泡加的光（FBX 本身无 Light），开灯全亮 / 关灯全灭。留空自动查找台灯根下所有 Light。")]
    private Light[] lights;

    [SerializeField, Tooltip("初始状态：进入 Play 时开关默认开启还是关闭（默认开启）。")]
    private bool startOn = true;

    /// <summary>当前是否开灯（初始值由 startOn 决定）。</summary>
    public bool IsOn { get; private set; }

    // --- 基线（Awake 捕获，永不累计） ---
    private Quaternion initialRotation;
    private Vector3 initialPosition;

    private void Awake()
    {
        ResolveLights();
        CaptureBaseline();
        IsOn = startOn;
        ApplyState();   // 按初始状态摆好拨杆与光源
    }

    /// <summary>交互框架入口：拨动开关。</summary>
    public void OnInteract() => Toggle();

    public void Toggle()
    {
        IsOn = !IsOn;
        ApplyState();
        GameEvents.SwitchToggled?.Invoke(transform.position);   // 开关拨动音效(注册式同步)
    }

    /// <summary>幂等设置开/关状态。</summary>
    public void SetOn(bool on)
    {
        if (IsOn == on) return;
        IsOn = on;
        ApplyState();
    }

    // === 存档 (ISceneSaveable:开关 = IsOn;恢复走幂等 SetOn,Awake 已在全新场景实例上摆好基线) ===
    public string SaveableType => "LampSwitchController";

    public string CaptureToJson()
    {
        return JsonUtility.ToJson(new LampState { on = IsOn });
    }

    public void RestoreFromJson(string json)
    {
        var s = JsonUtility.FromJson<LampState>(json);
        if (s == null)
        {
            Debug.LogWarning($"[LampSwitchController] {name}: 存档数据损坏，跳过开关状态恢复", this);
            return;
        }
        SetOn(s.on);
    }

    /// <summary>
    /// 台灯根 = 向上找 FBX 根节点（场景根一般名为 industrial_pipe_lamp_2k，FBX 内根节点为 industrial_pipe_lamp）。
    /// 从父级开始查找以排除开关自身；找不到则退回自身，需手动拖引用。
    /// </summary>
    private Transform FindLampRoot()
    {
        Transform t = transform.parent;
        while (t != null)
        {
            if (t.name == "industrial_pipe_lamp" || t.name == "industrial_pipe_lamp_2k")
                return t;
            t = t.parent;
        }
        return transform;
    }

    private void ResolveLights()
    {
        if (lights != null && lights.Length > 0)
            return;
        lights = FindLampRoot().GetComponentsInChildren<Light>(true);
    }

    private void CaptureBaseline()
    {
        initialRotation = transform.localRotation;
        initialPosition = transform.localPosition;
    }

    private void ApplyState()
    {
        ApplySwitchRotation();
        foreach (var l in lights)
            if (l != null)
                l.enabled = IsOn;
    }

    /// <summary>拨杆从基线重算旋转：关 = -角度（左），开 = +角度（右），绕自身初始局部轴。</summary>
    private void ApplySwitchRotation()
    {
        Vector3 axisLocal;
        switch (rotationAxis)
        {
            case RotAxis.X: axisLocal = Vector3.right; break;
            case RotAxis.Y: axisLocal = Vector3.up; break;
            default:        axisLocal = Vector3.forward; break;
        }
        float angle = IsOn ? switchAngle : -switchAngle;
        Quaternion q = Quaternion.AngleAxis(angle, initialRotation * axisLocal);
        transform.localRotation = q * initialRotation;

        if (pivotOffset != Vector3.zero)
        {
            Vector3 pivot = initialPosition + pivotOffset;                      // 铰点（父空间）
            transform.localPosition = pivot + q * (initialPosition - pivot);   // 绕铰点旋转
        }
    }
}
