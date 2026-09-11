using UnityEngine;

/// <summary>
/// 字幕触发区 —— 玩家（蜜蜂本体）**在触发区里**时，播放指定的字幕序列。
///
/// 判定是「人在不在里面」，不是「进入的那一瞬」：
///   OnTriggerEnter / OnTriggerStay 都只把 playerInside 记成 true，OnTriggerExit 记回 false，
///   真正的判定在 Update 里每帧做一次。这样不会因为错过一次进入回调就永远不触发
///   （组件在玩家已经站在里面时才启用、碰撞体是后加 / 改过大小的、蜜蜂极快一闪而过……）。
///   玩家身上只有一个碰撞体（Player 场景 Body 上的 SphereCollider），所以一个 bool 就够，
///   不需要计数。
///
/// 为什么 Update 里要挡一手 sequence.IsPlaying：
///   只要玩家在里面，Update 每帧都会走到这里。不挡的话每帧都调一次 Play()，
///   序列会被反复打断重来 —— 表现就是永远停在第 1 条、永远不往下走。
///   挡住之后语义才是：播完了、人还在里面，才轮到下一次触发（见 once）。
///
/// once 的两种行为：
///   - 勾上（默认）= 只播一次：第一次在里面时播，之后就算一直站着也不再播；
///   - 取消 = 只要在里面就反复播：播完停一下（序列自己的间隔时长），人还在就再来一遍。
///
/// 挂法：挂在带 Is Trigger 碰撞体的物体上（触发体自己勾 Is Trigger）。
/// 放哪：想让玩家飞到哪儿看到这句话，就把触发区摆在哪儿 —— 何时出现全靠位置，不在代码里调。
///
/// 常见坑：
///   1. 触发体要留厚度 / 开大：零厚度的盒子是 PhysX 里的退化几何，可能压根不产生重叠事件；
///      蜜蜂飞得快，薄片还可能一帧直接穿过去（Awake 里对薄盒子提示一次）。
///      建议整块门洞 / 整条走廊一块，别做薄面。
///   2. 身份判定只认玩家本体：Player 场景里带 Player 标签的是 Body（自带 SphereCollider
///      + Rigidbody），手里举着的钥匙 / 道具没有这个标签，不会替玩家提前触发。
///   3. once 只在本次场景实例内有效，不参与存档：读档 / 重进本关会重新播一遍（场景是新实例）。
///      这是刻意的 —— 重看一句旁白不是 bug（同 PaperReading「观察无持久状态」）。
///      真要「跨存档只播一次」的剧情关键句，去 SaveSystem 用 GetRunBool/SetRunBool 记一笔。
///
/// 字幕文本不在这里：文本直接写在各自的 TMP 节点上，本组件只把「该播哪串」交给 SubtitleSequence。
/// </summary>
public class SubtitleTrigger : MonoBehaviour
{
    [SerializeField, Tooltip("玩家在触发区里时要播放的字幕序列（要拖同一场景里的 SubtitleSequence）。")]
    private SubtitleSequence sequence;
    [SerializeField, Tooltip("只播一次（取消勾选 = 只要玩家还在里面，播完就再来一遍）。")]
    private bool once = true;

    private bool playerInside;      // 玩家当前是否在触发区里（进入 / 停留都置位，离开才清）
    private bool played;            // once 用：本场景实例内是否已经播过
    private bool warnedNoSequence;

    private void Awake()
    {
        // 零厚度触发体是 PhysX 的退化几何，可能不产生重叠事件（见头注释坑 1）
        if (TryGetComponent<BoxCollider>(out var box) && box.isTrigger && Mathf.Abs(box.size.z) < 0.02f)
            Debug.LogWarning($"[SubtitleTrigger] {name}: 触发体几乎零厚（z={box.size.z:F3}）：PhysX 可能不产生重叠事件，蜜蜂也可能一帧穿过去。建议给 0.2~0.4 厚度，或把触发区开大", this);
    }

    // ==================== 区域判定（三个回调只维护 playerInside 一个事实） ====================

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player")) playerInside = true;
    }

    private void OnTriggerStay(Collider other)
    {
        // 停留也置位：玩家早就站在里面、或错过了一次进入回调时，靠这里补上
        if (other.CompareTag("Player")) playerInside = true;
    }

    private void OnTriggerExit(Collider other)
    {
        if (other.CompareTag("Player")) playerInside = false;
    }

    // ==================== 触发 ====================

    private void Update()
    {
        if (!playerInside) return;
        if (once && played) return;

        if (sequence == null)
        {
            if (!warnedNoSequence)
            {
                warnedNoSequence = true;
                Debug.LogError($"[SubtitleTrigger] {name}: 没拖 SubtitleSequence —— 玩家在区域里但没东西可播。请在 Inspector 里把要播的字幕序列拖进来", this);
            }
            return;
        }

        if (sequence.IsPlaying) return;   // 正在播就别打断重来（见类注释）

        sequence.Play();
        played = true;
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (sequence == null)
            Debug.LogWarning($"[SubtitleTrigger] {name}: 没拖 SubtitleSequence —— 玩家在区域里也不会播任何字幕", this);

        bool hasTrigger = false;
        foreach (var col in GetComponentsInChildren<Collider>(true))
            if (col.isTrigger) { hasTrigger = true; break; }
        if (!hasTrigger)
            Debug.LogWarning($"[SubtitleTrigger] {name}: 本物体（含子物体）没有勾 Is Trigger 的碰撞体 —— 玩家进不来，永远不触发", this);
    }
#endif
}
