using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 音效管理器(单例):注册式同步 —— Awake 里把每个音效事件注册到 GameEvents 对应玩法事件上,
/// 玩法方法触发时在同一调用栈内同步播放。
/// 素材优先从 Assets/Resources/SFX/ 按文件名加载,缺失的文件用程序合成的占位音代替,
/// 并在启动时输出一次缺失清单警告(玩家把正式素材放进该文件夹即自动替换)。
/// 通过 [RuntimeInitializeOnLoadMethod] 自建 GameObject + DontDestroyOnLoad,无需任何场景编辑。
/// </summary>
public class AudioManager : MonoBehaviour
{
    public static AudioManager Instance { get; private set; }

    private AudioSource master;                                          // 2D 一次性音效
    private readonly Dictionary<SfxId, AudioClip> clips = new Dictionary<SfxId, AudioClip>();  // 缓存(持引用,防 Resources.UnloadUnusedAssets 回收)
    private readonly List<LoopHandle> loops = new List<LoopHandle>();                 // 活动循环
    private readonly Dictionary<Transform, LoopHandle> doorSlideLoops = new Dictionary<Transform, LoopHandle>();  // 滑门:门 → 马达循环
    private readonly List<string> missingFiles = new List<string>();     // 素材缺失清单(只警告一次)

    // 常驻循环句柄
    private LoopHandle wingLoop;         // 振翅
    private LoopHandle fallLoop;         // 落风
    private LoopHandle roomAmbient;      // 当前关卡环境音
    private Scene ambientScene;          // 当前环境音归属的关卡场景

    // ==================== 单例 ====================

    /// <summary>在任何场景加载前自建常驻管理器,保证玩法脚本 Awake 时必已就绪。</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void AutoCreate()
    {
        if (Instance != null) return;
        var go = new GameObject("[AudioManager]");
        DontDestroyOnLoad(go);
        go.AddComponent<AudioManager>();
    }

    private void Awake()
    {
        // 单例:防重复实例(与 LevelTransitionManager 同风格)
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        master = gameObject.AddComponent<AudioSource>();
        master.spatialBlend = 0f;      // 2D
        master.playOnAwake = false;

        LoadAllClips();
        RegisterEvents();

        // 环境音切换与灯嗡鸣的驱动(与关卡场景生命周期绑定)
        SceneManager.sceneLoaded += OnSceneLoaded;
        SceneManager.sceneUnloaded += OnSceneUnloaded;
    }

    // ==================== 注册表(核心:玩法方法 → 音效同步响应) ====================

    private void RegisterEvents()
    {
        GameEvents.BeeStateChanged += OnBeeStateChanged;
        GameEvents.CrawlStep += () => PlaySfx(SfxId.CrawlStep, 0.3f, UnityEngine.Random.Range(0.9f, 1.1f));
        GameEvents.PickUp += () => PlaySfx(SfxId.PickUp, 0.5f, UnityEngine.Random.Range(0.95f, 1.05f));
        GameEvents.Drop += pos => PlaySfxAt(SfxId.Drop, pos, 0.5f, UnityEngine.Random.Range(0.95f, 1.05f));
        GameEvents.AimGained += () => PlaySfx(SfxId.AimOn, 0.25f);
        GameEvents.AimLost += () => PlaySfx(SfxId.AimOff, 0.2f);
        GameEvents.SwitchToggled += pos => PlaySfxAt(SfxId.SwitchClick, pos, 0.6f, UnityEngine.Random.Range(0.9f, 1.1f));
        GameEvents.DoorOpen += pos => PlaySfxAt(SfxId.DoorOpen, pos, 0.7f);
        GameEvents.DoorClose += pos => PlaySfxAt(SfxId.DoorClose, pos, 0.7f);
        GameEvents.DoorSlideStart += OnDoorSlideStart;
        GameEvents.DoorSlideEnd += OnDoorSlideEnd;
        GameEvents.DoorClunk += pos => PlaySfxAt(SfxId.DoorClunk, pos, 0.5f);
        GameEvents.DoorDeny += pos => PlaySfxAt(SfxId.DoorDeny, pos, 0.6f);
        GameEvents.DoorLock += pos => PlaySfxAt(SfxId.DoorLock, pos, 0.5f);
        GameEvents.DoorUnlock += pos => PlaySfxAt(SfxId.DoorUnlock, pos, 0.5f);
        GameEvents.CardSuccess += pos => PlaySfxAt(SfxId.CardSuccess, pos, 0.7f);
        GameEvents.CardDeny += pos => PlaySfxAt(SfxId.CardDeny, pos, 0.6f);
        GameEvents.TransitionStart += () => PlaySfx(SfxId.TransitionWhoosh, 0.6f);
        GameEvents.LevelConfirm += () => PlaySfx(SfxId.LevelConfirm, 0.5f);
        GameEvents.UnloadFade += () => PlaySfx(SfxId.UnloadFade, 0.4f);
        GameEvents.LevelReady += OnLevelReady;
        GameEvents.FlickerCrackle += pos => PlaySfxAt(SfxId.FlickerCrackle, pos, 0.5f);

        // 注:管理器 DontDestroyOnLoad 常驻、单实例守卫,静态事件处理器不悬挂不重复;
        // 退出游戏 / 编辑器域重载时静态事件随域销毁,无需逐个反注册。
    }

    /// <summary>飞行状态切换:推导演奏/落地/坠地,并管理振翅与落风循环。</summary>
    private void OnBeeStateChanged(BeeFlightController.BeeState prev, BeeFlightController.BeeState next)
    {
        // 起飞:爬行 → 飞行(同时启振翅循环)
        if (prev == BeeFlightController.BeeState.Crawling && next == BeeFlightController.BeeState.Flying)
        {
            PlaySfx(SfxId.TakeOff, 0.6f);
            StopLoop(wingLoop);
            wingLoop = StartLoop(SfxId.WingBuzz, 0.35f);
        }

        // 离开飞行:停振翅(落地或坠落都会经过这里)
        if (prev == BeeFlightController.BeeState.Flying)
            StopLoop(wingLoop);

        // 落地:坠落 → 爬行
        if (prev == BeeFlightController.BeeState.Falling && next == BeeFlightController.BeeState.Crawling)
            PlaySfx(SfxId.Landing, 0.5f, UnityEngine.Random.Range(0.9f, 1.1f));

        // 开始坠落:爬行 → 坠落
        if (prev == BeeFlightController.BeeState.Crawling && next == BeeFlightController.BeeState.Falling)
            PlaySfx(SfxId.FallStart, 0.5f);

        // 落风循环跟随 Falling 状态
        if (next == BeeFlightController.BeeState.Falling)
        {
            StopLoop(fallLoop);
            fallLoop = StartLoop(SfxId.FallWind, 0.4f);
        }
        else
        {
            StopLoop(fallLoop);
        }
    }

    /// <summary>门开始滑动:启马达循环(中途反向先停旧的再启新的,不叠加)。</summary>
    private void OnDoorSlideStart(Transform door)
    {
        if (doorSlideLoops.TryGetValue(door, out var old))
        {
            old.Stop();
            doorSlideLoops.Remove(door);
        }
        if (door == null) return;   // 门体已随场景卸载销毁
        doorSlideLoops[door] = StartLoop(SfxId.DoorSlideLoop, 0.4f, door);
    }

    /// <summary>门滑动到位:停马达循环。</summary>
    private void OnDoorSlideEnd(Transform door)
    {
        if (door != null && doorSlideLoops.TryGetValue(door, out var loop))
        {
            loop.Stop();
            doorSlideLoops.Remove(door);
        }
    }

    /// <summary>关卡就绪(启动完成 / 出口门已开):停旧环境音、启新关卡环境音。</summary>
    private void OnLevelReady(Scene scene)
    {
        if (scene == ambientScene && roomAmbient != null) return;   // 同场景重复就绪不重启
        StopLoop(roomAmbient);
        ambientScene = scene;
        roomAmbient = StartLoop(SfxId.RoomAmbient, 0.5f);
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (mode != LoadSceneMode.Additive) return;   // 只处理关卡场景
        // 给本关卡每盏闪烁灯启 3D 嗡鸣循环(挂在灯体下,随场景卸载自动销毁)
        foreach (var light in FindObjectsOfType<FlickeringLight>())
        {
            if (light.gameObject.scene != scene) continue;
            StartLoop(SfxId.FlickerBuzz, 0.3f, light.transform);
        }
    }

    private void OnSceneUnloaded(Scene scene)
    {
        // 卸载的是当前环境音的归属关卡 → 停环境音(避免旧关音效残留到新关)
        if (scene == ambientScene)
        {
            StopLoop(roomAmbient);
            roomAmbient = null;
            ambientScene = default;
        }
    }

    // ==================== 播放 API ====================

    /// <summary>2D 一次性音效(玩家身上的声音用这个,不随距离衰减)。</summary>
    public void PlaySfx(SfxId id, float volume = 1f, float pitch = 1f)
    {
        if (master == null) return;
        var clip = GetClip(id);
        if (clip == null) return;
        master.pitch = pitch;
        master.PlayOneShot(clip, volume);
        master.pitch = 1f;
    }

    /// <summary>3D 一次性音效:在指定世界位置播放,带距离衰减。</summary>
    public void PlaySfxAt(SfxId id, Vector3 position, float volume = 1f, float pitch = 1f)
    {
        var clip = GetClip(id);
        if (clip == null) return;
        var go = new GameObject("SFX_" + id);
        go.transform.position = position;
        var src = go.AddComponent<AudioSource>();
        src.spatialBlend = 1f;
        src.minDistance = 2f;
        src.maxDistance = 30f;
        src.dopplerLevel = 0f;   // 关多普勒,避免移动中的音源音高扭曲
        src.pitch = pitch;
        src.volume = volume;
        src.clip = clip;
        src.Play();
        go.AddComponent<OneShotAudio>().Init(clip.length + 0.05f);
    }

    /// <summary>
    /// 启动循环音效。parent == null → 2D 全局循环(挂在管理器下,DontDestroyOnLoad 跨场景存活);
    /// parent != null → 3D 跟随循环(挂在 parent 下,随场景卸载销毁)。
    /// </summary>
    public LoopHandle StartLoop(SfxId id, float volume = 1f, Transform parent = null)
    {
        var clip = GetClip(id);
        if (clip == null) return null;
        var go = new GameObject("Loop_" + id);
        go.transform.SetParent(parent != null ? parent : transform, false);
        var src = go.AddComponent<AudioSource>();
        if (parent != null)
        {
            src.spatialBlend = 1f;
            src.minDistance = 2f;
            src.maxDistance = 30f;
        }
        src.dopplerLevel = 0f;
        src.loop = true;
        src.clip = clip;
        src.volume = volume;
        src.Play();
        var handle = go.AddComponent<LoopHandle>();
        loops.Add(handle);
        return handle;
    }

    /// <summary>停止循环(幂等:null / 已停 / 已销毁均可安全调用)。</summary>
    public void StopLoop(LoopHandle handle)
    {
        if (handle == null) return;
        handle.Stop();
    }

    internal void UnregisterLoop(LoopHandle handle)
    {
        loops.Remove(handle);
    }

    // ==================== 素材加载与占位合成 ====================

    private AudioClip GetClip(SfxId id) => clips.TryGetValue(id, out var clip) ? clip : null;

    private void LoadAllClips()
    {
        foreach (SfxId id in Enum.GetValues(typeof(SfxId)))
        {
            var def = SfxDefs[(int)id];
            var clip = Resources.Load<AudioClip>("SFX/" + def.FileName);
            if (clip == null)
            {
                clip = SynthPlaceholder(def);
                missingFiles.Add(def.FileName);
            }
            clips[id] = clip;
        }

        if (missingFiles.Count > 0)
        {
            Debug.LogWarning($"[AudioManager] 以下 {missingFiles.Count} 个音效素材缺失,已用程序生成的占位音代替。" +
                $"把正式音频文件放进 Assets/Resources/SFX/ 并重进 Play Mode 即可自动替换:\n  {string.Join("\n  ", missingFiles)}");
        }
    }

    private enum Wave { Sine, Square, Noise, BrownNoise }

    /// <summary>占位音参数:文件名同时是 Resources 素材匹配名,缺失时才用于合成。</summary>
    private struct SfxDef
    {
        public string FileName;
        public Wave Wave;
        public float F0;        // 起始频率
        public float F1;        // 结束频率(无扫频时 = F0)
        public float NoiseMix;  // 0..1:在基础波形上掺入白噪声的比例
        public float Duration;  // 秒
        public float Volume;    // 占位音相对音量
        public bool Loop;       // 循环片(头尾周期对齐 + 边缘淡入淡出,接缝无咔哒)
    }

    /// <summary>占位音合成参数表(顺序必须与 SfxId 枚举一致)。</summary>
    private static readonly SfxDef[] SfxDefs =
    {
        // 玩家飞行
        new SfxDef { FileName = "TakeOff",        Wave = Wave.Sine,       F0 = 180f, F1 = 420f, Duration = 0.30f, Volume = 0.5f  },  // 起飞 chirp
        new SfxDef { FileName = "Landing",        Wave = Wave.Sine,       F0 = 120f, F1 = 90f,  Duration = 0.15f, Volume = 0.6f  },  // 落地 thud
        new SfxDef { FileName = "FallStart",      Wave = Wave.BrownNoise, F0 = 500f, F1 = 120f, Duration = 0.35f, Volume = 0.5f  },  // 开始下落
        new SfxDef { FileName = "CrawlStep",      Wave = Wave.Sine,       F0 = 900f,           Duration = 0.06f, Volume = 0.4f  },  // 爬行脚步 tick
        new SfxDef { FileName = "WingBuzz",       Wave = Wave.Sine,       F0 = 150f, NoiseMix = 0.1f, Duration = 1.0f, Volume = 0.4f,  Loop = true },  // 振翅
        new SfxDef { FileName = "FallWind",       Wave = Wave.BrownNoise, Duration = 1.5f, Volume = 0.5f, Loop = true },                       // 落风
        // 交互
        new SfxDef { FileName = "PickUp",         Wave = Wave.Sine,       F0 = 500f, F1 = 900f, Duration = 0.12f, Volume = 0.5f  },  // 拾取 pop
        new SfxDef { FileName = "Drop",           Wave = Wave.Sine,       F0 = 140f, NoiseMix = 0.1f, Duration = 0.15f, Volume = 0.5f  },  // 放下 thud
        new SfxDef { FileName = "AimOn",          Wave = Wave.Sine,       F0 = 1600f,          Duration = 0.04f, Volume = 0.3f  },  // 描边获得
        new SfxDef { FileName = "AimOff",         Wave = Wave.Sine,       F0 = 1000f,          Duration = 0.04f, Volume = 0.3f  },  // 描边丢失
        new SfxDef { FileName = "SwitchClick",    Wave = Wave.Sine,       F0 = 600f, NoiseMix = 0.2f, Duration = 0.05f, Volume = 0.5f  },  // 开关拨动 click
        // 门
        new SfxDef { FileName = "DoorOpen",       Wave = Wave.Sine,       F0 = 180f, F1 = 90f,  Duration = 0.45f, Volume = 0.6f  },  // 开门
        new SfxDef { FileName = "DoorClose",      Wave = Wave.Sine,       F0 = 90f,  F1 = 180f, Duration = 0.45f, Volume = 0.6f  },  // 关门
        new SfxDef { FileName = "DoorSlideLoop",  Wave = Wave.Sine,       F0 = 110f, NoiseMix = 0.5f, Duration = 1.0f, Volume = 0.4f, Loop = true },  // 滑门马达
        new SfxDef { FileName = "DoorClunk",      Wave = Wave.Sine,       F0 = 75f,           Duration = 0.15f, Volume = 0.6f  },  // 门到位
        new SfxDef { FileName = "DoorDeny",       Wave = Wave.Square,     F0 = 110f,          Duration = 0.20f, Volume = 0.4f  },  // 上锁拒绝 buzz
        new SfxDef { FileName = "DoorLock",       Wave = Wave.Sine,       F0 = 1300f,          Duration = 0.05f, Volume = 0.4f  },  // 上锁 click
        new SfxDef { FileName = "DoorUnlock",     Wave = Wave.Sine,       F0 = 950f,           Duration = 0.06f, Volume = 0.4f  },  // 解锁 click
        // 读卡器
        new SfxDef { FileName = "CardSuccess",    Wave = Wave.Sine,       F0 = 880f, F1 = 1175f, Duration = 0.25f, Volume = 0.5f  },  // 刷卡成功 beep
        new SfxDef { FileName = "CardDeny",       Wave = Wave.Square,     F0 = 110f,          Duration = 0.20f, Volume = 0.4f  },  // 刷卡被拒 buzz
        // 关卡过渡
        new SfxDef { FileName = "TransitionWhoosh", Wave = Wave.Noise,    Duration = 1.2f, Volume = 0.5f },                            // 过渡开始 whoosh
        new SfxDef { FileName = "LevelConfirm",   Wave = Wave.Sine,       F0 = 660f, F1 = 880f, Duration = 0.30f, Volume = 0.5f  },  // 关卡确认 chime
        new SfxDef { FileName = "UnloadFade",     Wave = Wave.BrownNoise, Duration = 0.80f, Volume = 0.4f },                            // 卸载淡出
        new SfxDef { FileName = "RoomAmbient",    Wave = Wave.Sine,       F0 = 55f, NoiseMix = 0.4f, Duration = 2.0f, Volume = 0.25f, Loop = true },  // 房间环境音
        // 灯
        new SfxDef { FileName = "FlickerBuzz",    Wave = Wave.Sine,       F0 = 120f, NoiseMix = 0.3f, Duration = 0.5f, Volume = 0.3f, Loop = true },  // 灯嗡鸣
        new SfxDef { FileName = "FlickerCrackle", Wave = Wave.Noise,      Duration = 0.06f, Volume = 0.5f },                            // 断电噼啪
    };

    /// <summary>程序合成占位音:按 SfxDef 波形生成,循环片周期对齐 + 边缘淡入淡出,接缝无咔哒。</summary>
    private static AudioClip SynthPlaceholder(SfxDef def)
    {
        int sr = AudioSettings.outputSampleRate;

        // 循环片长度取基频整数周期(首尾相接无缝);一次性片按时长取样
        float baseFreq = Mathf.Max(def.F0, 1f);
        int samples = def.Loop
            ? Mathf.Max(1, Mathf.RoundToInt(Mathf.CeilToInt(def.Duration * baseFreq) * (float)sr / baseFreq))
            : Mathf.Max(1, Mathf.CeilToInt(def.Duration * sr));

        var data = new float[samples];
        double freq = def.F0;
        double fStep = (def.F1 - def.F0) / samples;
        float phase = 0f;
        float brown = 0f;

        int fadeIn = Mathf.Min(samples, (int)(sr * 0.008f));       // 8ms 淡入
        int fadeOut = def.Loop ? fadeIn : samples / 4;             // 循环:收尾防接缝;一次性:尾部平滑衰减

        for (int i = 0; i < samples; i++)
        {
            float sample;
            switch (def.Wave)
            {
                case Wave.Sine:
                    sample = Mathf.Sin(phase);
                    break;
                case Wave.Square:
                    sample = Mathf.Sign(Mathf.Sin(phase));
                    break;
                case Wave.Noise:
                    sample = UnityEngine.Random.Range(-1f, 1f);
                    break;
                default: // BrownNoise:一阶低通的白噪声(低频隆隆感),*4 补偿低通损失
                    brown = Mathf.Lerp(brown, UnityEngine.Random.Range(-1f, 1f), 0.05f);
                    sample = brown * 4f;
                    break;
            }
            if (def.NoiseMix > 0f)
                sample = Mathf.Lerp(sample, UnityEngine.Random.Range(-1f, 1f), def.NoiseMix);
            sample = Mathf.Clamp(sample, -1f, 1f);

            phase += (float)freq * Mathf.PI * 2f / sr;
            freq += fStep;

            // 包络
            if (i < fadeIn) sample *= (float)i / fadeIn;
            else if (i >= samples - fadeOut)
            {
                float k = (float)(samples - 1 - i) / Mathf.Max(1, fadeOut - 1);
                sample *= def.Loop ? k : Mathf.Sin(k * Mathf.PI * 0.5f);
            }

            data[i] = sample * def.Volume * 0.5f;   // 0.5 留余量防削波
        }

        var clip = AudioClip.Create($"{def.FileName}_Ph", samples, 1, sr, false);
        clip.SetData(data, 0);
        return clip;
    }

    // ==================== 每帧维护 ====================

    private void Update()
    {
        // 场景卸载销毁的 3D 循环(随音源物体)自动清扫
        loops.RemoveAll(l => l == null);

        // 门随场景卸载销毁后,清理其马达循环记录(正常情况 DoorSlideEnd 已移除)
        List<Transform> dead = null;
        foreach (var kv in doorSlideLoops)
            if (kv.Key == null || kv.Value == null)
            {
                if (dead == null) dead = new List<Transform>();
                dead.Add(kv.Key);
            }
        if (dead != null)
            foreach (var key in dead)
                doorSlideLoops.Remove(key);
    }
}
