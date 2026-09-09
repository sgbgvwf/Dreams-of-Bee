using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// 雾奖励控制器（Room_03）：达成时 "消失片" 整片关闭（组件 disable = 体积雾移出渲染），
/// "变清片" meanFreePath 瞬时切到目标值（越大越稀薄）；图案破坏时还原 Awake 捕获的原值（实时跟随）。
/// 每帧轮询镜像 SO（OnEnable 也立即 Apply 一次），瞬时应用、无过渡。
/// 哪片雾消失 / 哪片雾变清、变清目标值全部由 Inspector 拖拽/填写决定，代码不预设映射。
/// </summary>
public class FogReward : MonoBehaviour
{
    [SerializeField, Tooltip("达成镜像（与 Room_02 LampPatternWatcher 拖同一份资产）。")]
    private LampPatternStateSO state;

    [SerializeField, Tooltip("达成时整片消失的雾（拖 Local Volumetric Fog 组件；disable 组件即消失，破坏图案时恢复）。")]
    private LocalVolumetricFog fogToDisable;

    [SerializeField, Tooltip("达成时密度变清的雾（拖 Local Volumetric Fog 组件）。")]
    private LocalVolumetricFog fogToClear;

    [SerializeField, Tooltip("达成时变清雾的目标 meanFreePath（可视距离：越大雾越稀薄，须 > 0.05，建议 30~200）。")]
    private float clearTargetMeanFreePath = 100f;

    private bool lastSolved;                       // 已应用状态（防每帧重复 Apply）
    private float originalClearMeanFreePath;       // Awake 捕获原值，破坏图案时还原用

    private void Awake()
    {
        // 原值捕获必须先于 OnEnable 的首次 Apply（同物体生命周期内天然成立）
        if (fogToClear != null)
            originalClearMeanFreePath = fogToClear.parameters.meanFreePath;
    }

    private void OnEnable()
    {
        if (!Validate())
        {
            enabled = false;   // 拖齐引用后手动勾回启用
            return;
        }
        lastSolved = state.Solved;
        Apply(lastSolved);
    }

    private void Update()
    {
        bool now = state.Solved;
        if (now == lastSolved) return;
        lastSolved = now;
        Apply(now);
    }

    /// <summary>严格非兜底：引用缺失就醒目警告 + 停用，不许静默半工作。</summary>
    private bool Validate()
    {
        bool ok = true;

        if (state == null)
        {
            Debug.LogWarning($"[FogReward] {name}: State 未拖（LampPatternStateSO 资产）", this);
            ok = false;
        }
        if (fogToDisable == null)
        {
            Debug.LogWarning($"[FogReward] {name}: Fog To Disable 未拖（达成时整片消失的那片 Local Volumetric Fog）", this);
            ok = false;
        }
        if (fogToClear == null)
        {
            Debug.LogWarning($"[FogReward] {name}: Fog To Clear 未拖（达成时变清的那片 Local Volumetric Fog）", this);
            ok = false;
        }
        return ok;
    }

    private void Apply(bool solved)
    {
        if (fogToDisable != null)
            fogToDisable.enabled = !solved;   // disable = 该体积雾移出渲染，整片消失；true 重新注册

        if (fogToClear != null)
            fogToClear.parameters.meanFreePath = solved ? clearTargetMeanFreePath : originalClearMeanFreePath;   // 直接写字段，下一渲染帧生效
    }
}
