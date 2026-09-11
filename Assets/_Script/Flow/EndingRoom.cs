using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 结局房间的演出组件 —— 挂在 Room_Ending 场景里(整个游戏只有这一处会用)。
///
/// 是什么:玩家被结局门传送进这个纯色房间之后,本组件包办这一场演出 ——
/// 按结局换房间颜色 → 锁住玩家 → 同色幕布淡满 → 等字幕播完 → 缓慢变黑 → 回主菜单。
///
/// 一个结局一套"舞台"(stages 里一条 = 一个结局):
///   触发体 + SubtitleTrigger + SubtitleSequence + 字幕 TMP 节点,全放在这一条的 root 底下。
///   本组件按当前结局**只开这一套、关掉其它套** —— 字幕怎么播完全归字幕那套组件管
///   (玩家落地站进触发区,SubtitleTrigger 自己开播),本组件只是等它播完来收尾。
///   颜色也配在这一条上(房间 + 幕布共用),所以"这个结局长什么样、说什么话"是同一处配置。
///
/// 布置时机:颜色与舞台在【场景加载那一刻】就摆好(Start),不是等玩家落地 ——
/// 门面渲染的就是这个房间,颜色晚一步的话,开门那一下门洞里就是错的颜色。
/// 演出本身(锁输入 / 幕布 / 收尾)等"本场景成为当前关卡"才开始 —— 目的地关在刷卡那一刻(T0)
/// 就被预加载了,不当这道门会在玩家还在上一关时就开始演(判据与 SubtitleOnEvent 同一套)。
///
/// 房间怎么搭(见 LevelAnchor / PortalDoor 头注释):
///   - 房间里【没有门】—— 门在上一关,玩家是被那扇结局门传送进来的;
///   - 一个 Entry 锚点(落点):传送门把玩家相对出口锚点的位姿映射到它;
///     ⚠️ 每一场的触发体要罩住这个落点(玩家落地时得站在里面,字幕才会开播);
///   - 一台挂 PortalViewSync 的相机,Target Texture = 门面那张 RT,摆成 Entry 锚点的子物体
///     —— 刷卡后上一关那扇门的门洞里看到的就是这台相机渲出来的房间(一片纯色);
///   - 房间物件只有纯色(玩家什么都看不见),材质颜色由本组件按 stage 改;
///   - 幕布 = 一块全屏 Image(Canvas 的 sortingOrder 要盖过 Player 场景的 HUD(10)、
///     低于流程黑幕 Fade(100),建议 50)。
///
/// 计时走 scaled time(Time.deltaTime),与 SubtitleSequence 同一约定。结局里暂停是被禁的
/// (FlowStateSO.PauseAllowed 只认 InGame / DevDirectPlay),流程进结局前还会 ForceExitPause,
/// 所以不会出现"暂停把演出冻在半途"。
///
/// 配错不卡死:结局房间里玩家不能交互,停住就是死局 —— 所以找不到当前结局 / 没配这一条的舞台 /
/// 字幕压根没开播,一律报错之后**照样走完收尾回主菜单**(少一场演出或少一段文字,不是把人关在里面)。
/// </summary>
public class EndingRoom : MonoBehaviour
{
    /// <summary>一个结局在这个房间里的一套东西:颜色 + 自己的触发体 / 字幕。</summary>
    [Serializable]
    public class EndingStage
    {
        [Tooltip("结局 id —— 与 EndingCatalog 里登记的一致（卡上 Destination Ending Id 填的也是它）。")]
        public string endingId;

        [Tooltip("这个结局的房间颜色：房间物件的材质色 + 幕布色都用它。配色在场景里，代码不认识「白/黑」这种语义。")]
        public Color color = Color.black;

        [Tooltip("这一场的根：底下放触发体 + SubtitleTrigger + SubtitleSequence + 各条字幕 TMP 节点。\n本组件按当前结局只开这一套、关掉其它套，所以这一套默认关着也没关系。\n（根不能是本组件所在物体的祖先 —— 那会把自己一起关掉。）")]
        public GameObject root;

        [Tooltip("这一场的字幕序列（与 SubtitleTrigger 上拖的是同一个）—— 本组件只用它判断「播完了没有」，不负责开播。")]
        public SubtitleSequence sequence;
    }

    [Header("演出")]
    [SerializeField, Tooltip("每个结局一套：按当前结局只开对应那一套。")]
    private EndingStage[] stages;
    [SerializeField, Tooltip("全屏幕布 Image（先淡成结局色当保险，最后缓慢淡黑）。\n平时可以在场景里把它关着（编辑器里好看、直接 Play 这个房间也不会被全屏色挡住）—— 本组件会自己打开它。要关就关这个 Image 自己所在的物体，别关它上面的 Canvas（那样本组件打开不够）。")]
    private Image curtain;

    [Header("房间换色")]
    [SerializeField, Tooltip("房间那些纯色物件共用的那一个材质：按当前结局直接改它的颜色。\n⚠️ 改的是材质本身，所以这个材质要是结局房间专用的，别让别的场景共用它（否则退到主菜单后再进别的关，那关的物件会带着结局的颜色）。")]
    private Material roomMaterial;

    [Header("节奏")]
    [SerializeField, Tooltip("幕布从透明淡到结局色用多少秒（落地本来就是纯色无缝的，这一步只是「确实盖满」的保险）。")]
    private float curtainFadeSeconds = 1f;
    [SerializeField, Tooltip("收尾：幕布从结局色缓慢淡到全黑用多少秒。")]
    private float blackoutSeconds = 3f;

    /// <summary>等字幕开播的上限(秒)。超时 = 这一场的触发体没罩住落点,报错后照样收尾。</summary>
    private const float TextStartTimeoutSeconds = 5f;

    private EndingStage stage;     // 当前这一场(空 = 没有可演的结局)
    private bool started;          // 演出已开演(防重入)

    private void Start()
    {
        // 幕布平时可以在场景里关着(编辑器里好看,直接 Play 这个房间时也不会被全屏色挡住):
        // 到这一刻它就没必要再关着了 —— 先打开并压成全透明,后面淡入淡出才有东西可动。
        if (curtain != null)
        {
            curtain.gameObject.SetActive(true);
            SetCurtainAlpha(0f);
        }

        // 场景一加载就把这一场摆好:门面渲染的就是这个房间,颜色晚一步门洞里就是错的颜色。
        stage = FindStage(GameFlowManager.CurrentEndingId);
        SetStageActive(stage);     // 只武装当前结局那一套(它的触发体随之启用)
        if (stage != null)
        {
            ApplyColor(stage.color);
            Debug.Log($"[EndingRoom] 结局 [{stage.endingId}] 这一场已就位（颜色与字幕都归 root「{(stage.root != null ? stage.root.name : "?")}」）", this);
        }
    }

    private void Update()
    {
        if (started) return;
        if (!IsMySceneCurrentLevel()) return;   // 预加载窗口里不当真，见类注释
        started = true;
        StartCoroutine(PlayRoutine());
    }

    private IEnumerator PlayRoutine()
    {
        LockPlayer();

        if (curtain != null)
        {
            SetCurtainAlpha(0f);
            yield return FadeCurtainRoutine(1f, curtainFadeSeconds);   // 同色幕布淡满(视觉上看不出变化)
        }

        // 字幕由这一场的 SubtitleTrigger 在玩家进入触发区时开播 —— 这里只等它播完。
        // 先等它开播(玩家落地就在触发区里,正常一两帧内就开;超时说明触发体没罩住落点)。
        if (stage?.sequence != null)
        {
            float wait = 0f;
            while (!stage.sequence.IsPlaying && wait < TextStartTimeoutSeconds)
            {
                wait += Time.deltaTime;
                yield return null;
            }
            if (!stage.sequence.IsPlaying)
                Debug.LogError($"[EndingRoom] 等了 {TextStartTimeoutSeconds} 秒这一场的字幕都没开播 —— 多半是触发体没罩住玩家落点（Entry 锚点）。直接收尾回主菜单", this);

            while (stage.sequence.IsPlaying) yield return null;
        }

        yield return BlackoutRoutine();
        Debug.Log("[EndingRoom] 结局演出结束，回主菜单", this);
        GameFlowManager.EndingConfirmed();
    }

    // ==================== 判定 / 布置 ====================

    /// <summary>本场景是不是"当前关卡"(判据与 SubtitleOnEvent 同一套,理由见类注释)。
    /// 没有过渡系统 / 拿不到当前关(直接 Play 这个房间调试)一律放行。</summary>
    private bool IsMySceneCurrentLevel()
    {
        var ltm = LevelTransitionManager.Instance;
        if (ltm == null) return true;

        string current = ltm.CurrentLevelPath;
        if (string.IsNullOrEmpty(current)) return true;

        var scene = gameObject.scene;
        if (!scene.IsValid() || string.IsNullOrEmpty(scene.path)) return true;

        return current == scene.path;
    }

    private EndingStage FindStage(string endingId)
    {
        if (string.IsNullOrEmpty(endingId))
        {
            Debug.LogError($"[EndingRoom] {name}: 现在没有正在演出的结局（GameFlowManager.CurrentEndingId 为空）—— 多半是从一张指向结局房间的旧档直接进来的。这一场没有结局可演，落地后直接回主菜单", this);
            return null;
        }

        if (stages != null)
            foreach (var s in stages)
                if (s != null && s.endingId == endingId) return s;

        Debug.LogError($"[EndingRoom] {name}: stages 里没有结局 [{endingId}] 这一条 —— 房间颜色与该播的字幕都不知道用哪一套。请在 Inspector 里给这个结局加一条 stage", this);
        return null;
    }

    /// <summary>只开当前这一套(传 null = 全关):字幕那套组件随 root 一起启用/停用。</summary>
    private void SetStageActive(EndingStage active)
    {
        if (stages == null) return;
        foreach (var s in stages)
        {
            if (s?.root == null) continue;
            s.root.SetActive(s == active);
        }
    }

    private void ApplyColor(Color color)
    {
        if (roomMaterial == null)
            Debug.LogError($"[EndingRoom] {name}: 没有拖房间材质 —— 门面渲染的就是这个房间，材质不换色的话开门那一下门洞里是错的颜色。请把房间物件共用的那个材质拖进来", this);
        else
            roomMaterial.color = color;   // 全房间共用一个材质:直接改它的颜色

        if (curtain != null)
        {
            var c = curtain.color;
            curtain.color = new Color(color.r, color.g, color.b, c.a);   // 只取 RGB,alpha 归淡入管
        }
    }

    /// <summary>结局期间不允许交互:锁住玩家的输入(与落水救援 / 全屏阅读同一接管机制)。
    /// 只锁输入不冻结物理 —— 蜜蜂会照常下坠,但整屏一片纯色,看不出来。</summary>
    private void LockPlayer()
    {
        var go = GameObject.FindGameObjectWithTag("Player");
        if (go == null)
        {
            Debug.LogWarning($"[EndingRoom] {name}: 找不到玩家（tag \"Player\"）—— 结局期间没能锁住交互", this);
            return;
        }
        var flight = go.GetComponent<BeeFlightController>();
        if (flight == null)
        {
            Debug.LogWarning($"[EndingRoom] {name}: 玩家身上没有 BeeFlightController —— 结局期间没能锁住交互", this);
            return;
        }
        flight.InputLocked = true;
    }

    // ==================== 幕布 ====================

    private IEnumerator BlackoutRoutine()
    {
        if (curtain == null) yield break;
        var c = curtain.color;
        curtain.color = new Color(0f, 0f, 0f, c.a);   // 从结局色出发 → 黑
        yield return FadeCurtainRoutine(1f, blackoutSeconds);
    }

    private IEnumerator FadeCurtainRoutine(float targetAlpha, float duration)
    {
        float start = curtain.color.a;
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;   // scaled:与 SubtitleSequence 同约定(结局态不可能暂停,见类注释)
            SetCurtainAlpha(Mathf.Lerp(start, targetAlpha, Mathf.Clamp01(t / Mathf.Max(0.001f, duration))));
            yield return null;
        }
        SetCurtainAlpha(targetAlpha);
    }

    private void SetCurtainAlpha(float alpha)
    {
        if (curtain == null) return;
        var c = curtain.color;
        curtain.color = new Color(c.r, c.g, c.b, Mathf.Clamp01(alpha));
    }
}
