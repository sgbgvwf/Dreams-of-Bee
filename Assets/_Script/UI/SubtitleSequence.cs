using System;
using UnityEngine;

/// <summary>
/// 字幕播放器 —— 只做一件事：按顺序把一串字幕节点亮起来、到点灭掉、再亮下一条。
///
/// 文本不在这里：字幕文字直接写在各自的 TMP 节点上（场景里看得见、改得动），
/// 本组件只持有「亮哪个物体、亮多久」，不碰任何文字内容 —— 显示什么由场景决定，
/// 什么时候显示由这里决定，两边互不干扰。
///
/// 场景化接线（与 ReadingOverlay / FadeOverlay 同款，视觉在场景里摆、脚本只留行为）：
///   挂在字幕所在的父节点上，lines 里按播放顺序拖入每条字幕的视觉根（通常就是个带 TMP
///   的节点；想让底衬一起显隐就把底衬也拖进来）。本组件只对拖进来的物体 SetActive，
///   每条单独配停留秒数 —— 长句给长、短句给短，不用迁就统一节奏。
///
/// 计时走 scaled time（Time.deltaTime）：暂停（timeScale = 0）时字幕停在原地不动，
/// 与全项目「没有任何 unscaledDeltaTime / WaitForSecondsRealtime 用法」的约定一致
/// （见 PauseMenu 类注释）。本组件刻意不用 unscaled / WaitForSecondsRealtime。
///
/// 每条的时间由两段组成：显示时长（亮着停多久）+ 间隔时长（灭掉之后空多久才亮下一条）。
/// 两段都按「本条」配 —— 长句给长、短句给短，中间想留白就填间隔，留 0 就是紧挨着切。
/// 最后一条的间隔不适用：后面没有下一条了，播完直接收场。
///
/// 显隐语义：
///   - Awake 先把所有条目藏好：场景里为了让作者看清位置而留着的激活状态，进 Play 不该闪一下；
///   - Play() = 全部藏好 → 亮第一条 → 每条「亮 seconds 秒 → 灭 → 空 interval 秒」→ 播完全藏；
///   - Stop() = 立刻全藏并中止（中途收场用），没在播时调用也安全。
///
/// 配错不拦播（和 PaperReading 的「拒绝播放」刻意不同）：没拖 target 的那条就是不亮，
/// 但它的显示时长照走 —— 前面一条配错不会把后面字幕的节奏带偏。seconds 配成负数按 0 算
/// （编辑期由 OnValidate 直接改回 0）；显示时长为 0 的条目不点亮（亮一帧再灭就是闪一下，
/// 不如不亮），时间同样照走。一条都没配时 Play 等于没开播。
/// </summary>
public class SubtitleSequence : MonoBehaviour
{
    /// <summary>一条字幕：亮哪个物体、亮多久、灭掉之后空多久。</summary>
    [Serializable]
    public struct Line
    {
        [Tooltip("这条字幕的视觉根（通常是个带 TMP 的节点；底衬想一起显隐就拖它）。播到它时 SetActive(true)，过后 SetActive(false)。")]
        public GameObject target;
        [Tooltip("显示时长：这条亮着停多少秒（长句给长、短句给短）。0 = 不点亮、直接过去；配成负数会被自动改成 0。")]
        public float seconds;
        [Tooltip("间隔时长：这条灭掉之后、下一条亮起之前空多少秒（留 0 = 立刻切下一条）。最后一条后面没有下一条，不用配。")]
        public float interval;
    }

    [SerializeField, Tooltip("按播放顺序排列的字幕条目：从头到尾依次亮起，每条停够自己的秒数，全部播完自动全藏。")]
    private Line[] lines;

    /// <summary>正在播放中（Play 之后、全部播完之前为 true）。</summary>
    public bool IsPlaying => index >= 0;

    private int index = -1;       // 当前处理到第几条；-1 = 没在播
    private float elapsed;        // 当前阶段（显示 / 间隔）已经过了多久
    private bool inGap;           // true = 本条已灭、正走在「间隔」里（此时 index 仍指着刚灭的那条）

    private void Awake()
    {
        // 别让编辑期为了看清位置而留着的激活状态在开局闪一下
        HideAll();
    }

    // ==================== 播放入口 ====================

    /// <summary>
    /// 从头播一遍（已在播时重复调用 = 打断当前进度、从第一条重来）。
    /// 配错不拦播：没拖 target 的条目不亮、时间照走；一条都没配时等于没开播。
    /// </summary>
    public void Play()
    {
        HideAll();

        if (lines == null || lines.Length == 0)   // 没有条目：没有时间线可走，不进播放态
        {
            index = -1;
            elapsed = 0f;
            inGap = false;
            return;
        }

        index = 0;
        elapsed = 0f;
        inGap = false;
        Show(0);
    }

    /// <summary>立刻中止并全部藏好（中途收场 / 手动清理用）。没在播时也安全。</summary>
    public void Stop()
    {
        HideAll();
        index = -1;
        elapsed = 0f;
        inGap = false;
    }

    // ==================== 推进 ====================

    private void Update()
    {
        if (index < 0) return;

        elapsed += Time.deltaTime;   // scaled：暂停时 +0，字幕跟着世界一起冻住

        if (inGap)
        {
            // 上一条已灭，正走在「间隔」里：间隔走完就亮下一条
            // （inGap 只在后面确实还有条目时才置位，所以这里 index++ 一定不越界）
            if (elapsed < lines[index].interval) return;
            inGap = false;
            elapsed = 0f;
            index++;
            Show(index);
            return;
        }

        // 本条亮着：显示时长走完就灭
        if (elapsed < DisplaySeconds(lines[index])) return;
        SetVisible(index, false);
        elapsed = 0f;                                 // 每段都从起点算满自己的秒数，不结转余额

        if (index + 1 >= lines.Length)                // 最后一条：没有下一条了，间隔不适用，直接收场
        {
            index = -1;
            return;
        }

        if (lines[index].interval > 0f)               // 后面还有条目：先空足间隔再切
        {
            inGap = true;
            return;
        }

        index++;                                      // 没配间隔：立刻切下一条
        Show(index);
    }

    // ==================== 内部 ====================

    /// <summary>点亮第 i 条。显示时长是 0 的不点 —— 亮一帧再灭就是闪一下，等于没显示；时间照走。</summary>
    private void Show(int i)
    {
        if (DisplaySeconds(lines[i]) <= 0f) return;
        SetVisible(i, true);
    }

    /// <summary>这条实际亮多久：负数按 0 算（编辑期 OnValidate 会直接改成 0，这里是运行期同款口径）。</summary>
    private static float DisplaySeconds(Line line) => line.seconds > 0f ? line.seconds : 0f;

    private void SetVisible(int i, bool visible)
    {
        var go = lines[i].target;
        if (go != null) go.SetActive(visible);
    }

    private void HideAll()
    {
        if (lines == null) return;
        for (int i = 0; i < lines.Length; i++)
            SetVisible(i, false);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (lines == null || lines.Length == 0)
        {
            Debug.LogWarning($"[SubtitleSequence] {name}: 还没有任何字幕条目 —— 播的时候会直接跳过。请把每条字幕的视觉根按顺序拖进来", this);
            return;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].seconds < 0f)     // 负数不当错误拦着：直接改回 0，这条就是「不亮、直接过去」
            {
                lines[i].seconds = 0f;
                Debug.LogWarning($"[SubtitleSequence] {name}: 第 {i + 1} 条显示时长是负数 —— 已自动改成 0", this);
            }

            if (lines[i].target == null)
                // Debug.LogWarning($"[SubtitleSequence] {name}: 第 {i + 1} 条没拖 target —— 这条不会亮，但它的显示时长照走（后面的字幕节奏不受影响）", this);

            if (lines[i].interval < 0f)
                Debug.LogWarning($"[SubtitleSequence] {name}: 第 {i + 1} 条间隔时长是负数 —— 按 0 处理（立刻切下一条），请改成非负数", this);
        }
    }
#endif
}
