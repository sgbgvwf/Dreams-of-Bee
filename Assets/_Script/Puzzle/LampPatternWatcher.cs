using UnityEngine;

/// <summary>
/// 八灯图案监听器（Room_02）：逐盏比对灯的开关状态与期望图案，全部一致 = 达成，破坏即恢复（实时跟随，非锁定）。
/// 灯没有按盏粒度的事件（读档恢复走静默 SetOn），因此 LateUpdate 轮询 LampSwitchController.IsOn；
/// 图案状态翻转时才写镜像 SO，避免每帧重复写。
/// 槽位引用需手动拖 prefab 实例内 industrial_pipe_lamp_switch 节点上的 LampSwitchController
/// （实例内组件无法可靠手写进场景 YAML，只能编辑器里拖）。
/// </summary>
public class LampPatternWatcher : MonoBehaviour
{
    /// <summary>一盏灯的期望槽位：拖进灯控制器引用 + 勾选该盏期望亮/灭。</summary>
    [System.Serializable]
    public class LampSlot
    {
        [Tooltip("拖：某盏灯 prefab 实例下 industrial_pipe_lamp_switch 节点上的 LampSwitchController。")]
        public LampSwitchController switchController;

        [Tooltip("该盏灯的期望状态：勾 = 亮，不勾 = 灭（图案全符才达成）。")]
        public bool expectedOn = true;
    }

    [SerializeField, Tooltip("达成镜像（与 Room_03 FogReward 拖同一份资产）。")]
    private LampPatternStateSO state;

    [SerializeField, Tooltip("8 盏灯逐盏槽位（数量与灯数对应；留空/引用缺失会在启用时警告并停用本组件）。")]
    private LampSlot[] lampSlots = System.Array.Empty<LampSlot>();

    [SerializeField, Tooltip("图案达成/破坏翻转时打日志（默认开，便于验证）。")]
    private bool logTransitions = true;

    private bool lastSolved;   // 上一帧镜像值（OnEnable 对齐，防每帧重复写）

    private void OnEnable()
    {
        if (!Validate())
        {
            enabled = false;   // 拖齐引用后手动勾回启用
            return;
        }
        lastSolved = state.Solved;
    }

    /// <summary>严格非兜底：引用缺失就醒目警告 + 停用，不许静默半工作。</summary>
    private bool Validate()
    {
        bool ok = true;

        if (state == null)
        {
            Debug.LogWarning($"[LampPatternWatcher] {name}: State 未拖（LampPatternStateSO 资产）", this);
            ok = false;
        }
        if (lampSlots == null || lampSlots.Length == 0)
        {
            Debug.LogWarning($"[LampPatternWatcher] {name}: Lamp Slots 为空，请把 8 盏灯的 LampSwitchController 拖满槽位", this);
            ok = false;
        }
        else
        {
            for (int i = 0; i < lampSlots.Length; i++)
            {
                if (lampSlots[i] == null || lampSlots[i].switchController == null)
                {
                    Debug.LogWarning($"[LampPatternWatcher] {name}: Lamp Slots 第 {i} 槽未拖灯（LampSwitchController）", this);
                    ok = false;
                }
            }
        }
        return ok;
    }

    private void LateUpdate()
    {
        bool now = true;
        foreach (var slot in lampSlots)
        {
            if (slot.switchController.IsOn != slot.expectedOn)
            {
                now = false;
                break;
            }
        }
        if (now == lastSolved) return;

        lastSolved = now;
        state.PushSolved(now);
        if (logTransitions)
            Debug.Log($"[LampPatternWatcher] {name}: 图案达成={now}", this);
    }
}
