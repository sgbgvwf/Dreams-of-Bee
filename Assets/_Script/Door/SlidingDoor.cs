 using UnityEngine;
using System.Collections;

/// <summary>
/// Slides a door panel sideways on demand: call OpenDoor(), CloseDoor() or
/// ToggleDoor() from any script (e.g. a CardReader). The door is fully
/// reactive - it does nothing while idle; each call starts a coroutine that
/// slides the door over duration seconds, then the coroutine ends. Assign
/// the door's root Transform in the Inspector.
/// The door slides along slideDirection (default local +X, "opens to the
/// door's own right") by slideDistance units. The slideCurve maps slide progress (0..1) to
/// displacement (0..1): a straight line is constant speed, an S-curve
/// (the default) gives smooth ease-in / ease-out.
/// The closed position is captured on first use, so the door can be freely
/// posed in the scene. The script drives only the assigned door Transform,
/// so every child (the model parts, the Area Lights, LightColorAlternator on
/// a child) travels together. Safe to combine with the light scripts.
/// Attach to any GameObject and drag the door root into the Inspector.
/// Play Mode only.
///
/// Note: doors are NOT Interactable - no pickup, no outline. They are driven
/// by CardReader / LevelTransitionManager only.
/// </summary>
public class SlidingDoor : MonoBehaviour, ISceneSaveable
{
    [SerializeField, Tooltip("The door's root Transform to slide. Drag it here.")]
    private Transform door;

    [SerializeField, Tooltip("Slide direction in the door's own local space (relative to its pose). Default +X: opens toward the door's own right, even when the door is posed at an angle.")]
    private Vector3 slideDirection = Vector3.right;

    [SerializeField, Tooltip("How far the door slides (world units). Open position = closed position + slideDirection * slideDistance.")]
    [Min(0.01f)]
    private float slideDistance = 3f;

    [SerializeField, Tooltip("Seconds a full open or close takes.")]
    [Min(0.01f)]
    private float duration = 1f;

    [SerializeField, Tooltip("Maps slide progress (0..1) to displacement (0..1). Straight line = constant speed, S-curve = smooth acceleration/deceleration.")]
    private AnimationCurve slideCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [SerializeField, Tooltip("Test switch: tick it during Play Mode to open, untick to close. Not a real signal input.")]
    private bool open;

    [SerializeField, Tooltip("初始是否上锁：上锁时忽略一切开门请求（关门始终允许）。关卡切换系统用它在玩家通过后防止回头。")]
    private bool startLocked;

    [SerializeField, Tooltip("近关夹持位置(0..1)：开门途中被夹持时停在的位置。0=全关，1=全开。夹持期间门缝小于玩家直径，既是物理屏障也是视觉屏障（加载等待期的无 UI 遮挡）。")]
    [Range(0.05f, 0.3f)]
    private float holdProgress = 0.12f;

    [SerializeField, Tooltip("所属关卡索引（与 LevelTransitionManager 关卡列表一致；-1=不校验）。关卡切换系统用它校验出口门归属，防止出口锚点误配别的关的门（钥匙已无归属关卡概念，刷卡去哪由卡上目的地决定）")]
    private int levelIndex = -1;

    // --- Lock / hold state (driven by LevelTransitionManager) ---
    private bool locked;        // 锁：只阻止"开门"，不阻止"关门"
    private bool holdOpen;      // 近关夹持：开门途中钳制在 holdProgress

    // --- Baseline captured on first use (door may be posed in the scene) ---
    private Vector3 closedPosition;
    private Vector3 openPosition;
    private bool hasBaseline;

    // --- Slide state ---
    private float progress;     // 0 = closed, 1 = open; survives between coroutines
    private bool targetOpen;    // where the door is headed
    private Coroutine slideRoutine;

    private bool lastOpen;   // last value the test switch drove the door with
    private bool warnedMissingDoor;

    private void Awake()
    {
        locked = startLocked;
    }

    private void Update()
    {
        // Test-only: a change of the Inspector checkbox drives the door;
        // external signals (e.g. CardReader) are never overridden.
        if (open != lastOpen)
        {
            lastOpen = open;
            SignalTo(open);
        }
    }

    /// <summary>Open the door. No-op when it is already open or opening.</summary>
    public void OpenDoor()
    {
        SignalTo(true);
    }

    /// <summary>Close the door. No-op when it is already closed or closing.</summary>
    public void CloseDoor()
    {
        SignalTo(false);
    }

    /// <summary>Flip the door's state; reverses direction smoothly mid-flight.</summary>
    public void ToggleDoor()
    {
        SignalTo(!targetOpen);
    }

    /// <summary>门是否处于锁定状态。</summary>
    public bool IsLocked => locked;

    /// <summary>所属关卡索引（-1 = 未配置，不校验）。</summary>
    public int LevelIndex => levelIndex;

    /// <summary>
    /// 设置锁定：上锁后忽略一切开门请求（关门始终允许）。
    /// 关卡切换系统在玩家通过后上锁，防止玩家回头再开门。
    /// 幂等：状态没变化直接返回，不重播音效（存档恢复 / 重复上锁防双响）。
    /// </summary>
    public void SetLocked(bool value)
    {
        if (locked == value) return;
        locked = value;

        // 上锁/解锁音效(启动预锁会有一声轻响,可接受)
        var pos = door != null ? door.position : transform.position;
        if (value) GameEvents.DoorLock?.Invoke(pos);
        else GameEvents.DoorUnlock?.Invoke(pos);
    }

    /// <summary>
    /// 近关夹持开关：开门途中把进度钳制在 holdProgress（加载等待期的无 UI 屏障）。
    /// 解除夹持后门自动继续开完，无需重新发信号。
    /// </summary>
    public void SetHoldOpen(bool active)
    {
        holdOpen = active;
    }

    /// <summary>门是否已完全关闭（进度 0）。关卡切换系统用它判断关门完成后再卸载场景。</summary>
    public bool IsFullyClosed => progress <= 0f;

    /// <summary>门板根 Transform（滑动的目标对象）。传送门系统读取它以确定门洞尺寸。</summary>
    public Transform DoorPanel => door;

    /// <summary>门板上的碰撞体（尺寸 = 门洞尺寸）。在门板对象及其子物体上查找，无门板时回退到组件自身。</summary>
    public Collider DoorPanelCollider =>
        door != null ? door.GetComponentInChildren<Collider>(true) : GetComponentInChildren<Collider>(true);

    // === 存档 (ISceneSaveable:门 = locked + 目标开闭态;恢复先于任何游玩帧,基线是场景摆好的关闭位) ===
    public string SaveableType => "SlidingDoor";

    public string CaptureToJson()
    {
        return JsonUtility.ToJson(new DoorState { locked = IsLocked, open = IsOpen });
    }

    public void RestoreFromJson(string json)
    {
        var s = JsonUtility.FromJson<DoorState>(json);
        if (s == null)
        {
            Debug.LogWarning($"[SlidingDoor] {name}: 存档数据损坏，跳过门状态恢复", this);
            return;
        }
        SetLocked(s.locked);
        if (s.open) OpenDoor();
        else CloseDoor();
    }

    private void SignalTo(bool opening)
    {
        if (door == null)
        {
            if (!warnedMissingDoor)
            {
                warnedMissingDoor = true;
                Debug.LogWarning($"[SlidingDoor] No door assigned on {name}. " +
                    "Drag the door's root Transform into the Inspector.");
            }
            return;
        }

        // 锁定时忽略开门请求（关门不受锁限制，避免玩家被卡在半开门状态）
        if (opening && locked)
        {
            GameEvents.DoorDeny?.Invoke(door.position);   // 上锁拒绝音效(注册式同步)
            return;
        }

        if (!hasBaseline) CaptureBaseline();
        if (opening == targetOpen) return;   // already headed there: no-op
        targetOpen = opening;
        StartSlide();

        // 注册式音效同步:门真正开始滑动时广播(OpenDoor/CloseDoor/ToggleDoor 全部汇入此处,守卫之后才响,不空响)
        if (opening) GameEvents.DoorOpen?.Invoke(door.position);
        else GameEvents.DoorClose?.Invoke(door.position);
        GameEvents.DoorSlideStart?.Invoke(door);   // 滑门马达循环(中途反向时处理器先停旧再启新)
    }

    /// <summary>True while the door's target state is open.</summary>
    public bool IsOpen => targetOpen;

    /// <summary>Toggle from the Inspector context menu for quick Play Mode testing.</summary>
    [ContextMenu("Toggle Door")]
    private void ToggleDoorFromInspector()
    {
        ToggleDoor();
    }

    private void StartSlide()
    {
        // A new signal mid-flight cancels the old slide and continues from
        // the current progress, so a reversal turns around smoothly.
        if (slideRoutine != null) StopCoroutine(slideRoutine);
        slideRoutine = StartCoroutine(SlideTo(targetOpen));
    }

    private IEnumerator SlideTo(bool opening)
    {
        float target = opening ? 1f : 0f;
        while (progress != target)
        {
            progress = Mathf.MoveTowards(progress, target, Time.deltaTime / duration);
            // 近关夹持：开门途中把进度钳在 holdProgress，门停在近关位（目标永远未达 → 协程持续运行）；
            // 解除夹持后进度继续推进，门自动开完。夹持只作用于"开门"，关门不受影响。
            if (opening && holdOpen)
                progress = Mathf.Min(progress, holdProgress);
            door.position = Vector3.Lerp(closedPosition, openPosition, slideCurve.Evaluate(progress));
            yield return null;
        }
        // Snap to the exact endpoint; the coroutine ends here, so an idle
        // door never runs any per-frame work.
        door.position = Vector3.Lerp(closedPosition, openPosition, slideCurve.Evaluate(target));
        slideRoutine = null;

        // 滑行结束:停马达循环 + 到位碰撞音
        GameEvents.DoorSlideEnd?.Invoke(door);
        GameEvents.DoorClunk?.Invoke(door.position);
    }

    private void CaptureBaseline()
    {
        closedPosition = door.position;
        // slideDirection 是门板自身的局部方向：旋转摆放的门也沿自己轴向滑动。
        // 开门期间门板只平移不旋转，所以基线时换算一次即可，方向不会漂移。
        openPosition = closedPosition + door.TransformDirection(slideDirection) * slideDistance;
        hasBaseline = true;
    }
}
