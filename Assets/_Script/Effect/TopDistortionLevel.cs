using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering.HighDefinition;

/// <summary>
/// TopDistortion 全屏效果的按关卡启停(关卡级开关脚本)。
///
/// 规则:在「要启用 TopDistortion 的关卡」场景里放一个空物体挂上本脚本即可 ——
/// 该关卡加载时自动点亮效果、该关卡卸载时自动关闭;没放本脚本的关卡效果保持关闭。
/// 效果本体是 Player 场景 "Volume" 物体上 Custom Pass Volume 里的 FullScreenCustomPass
/// (即 Custom Pass 列表里的那一项,材质 = TopDistortion.mat),在场景里默认禁用,
/// 由各关卡的挂点按自己的生命周期点亮 / 熄灭,关卡之间互不感知。
///
/// 场景手动配置:
///  - 效果本体(全项目只有一处):Player 场景 → Volume 物体 → Custom Pass Volume →
///    FullScreenCustomPass 项保持"未勾选"状态即可,本脚本只按材质名找它;
///  - 关卡挂点:目标关卡场景根下建空物体(如名为 TopDistortion),挂本脚本。
///
/// 关卡过渡窗口(新关已加载、旧关未卸载)内若新旧两关都挂了脚本,实例并存,
/// 最后一个实例随旧关卸载消失后才真正关闭 —— 不会在过渡中来回闪。
///
/// 注意:
///  - 匹配方式按材质名(与 TopDistortion.mat 同名);新建同类全屏效果请复制本脚本
///    并改类名与 k_TargetMaterialName;
///  - 单场景直接 Play 关卡(开发者直玩,Player 场景未加载)时找不到效果本体,
///    只会留一条日志,属正常 —— 本脚本不做其他兜底。
/// </summary>
public class TopDistortionLevel : MonoBehaviour
{
    /// <summary>目标效果的材质名(匹配 FullScreenCustomPass 的 fullscreenPassMaterial)。</summary>
    private const string k_TargetMaterialName = "TopDistortion";

    /// <summary>当前启用中(存活)的关卡标记数:最后一个销毁前不真正关闭,防过渡窗口闪烁。</summary>
    private static int activeCount;

    private void OnEnable()
    {
        activeCount++;
        // 玩家场景可能与关卡并行加载(本关先到、Player 后到 → 效果本体还没出现):
        // 先试一次;找不到就等 sceneLoaded,Player 场景一就绪立即补点亮
        if (!TrySetPassEnabled(true))
            SceneManager.sceneLoaded += OnAnySceneLoaded;
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnAnySceneLoaded;
        activeCount--;
        if (activeCount <= 0)
            TrySetPassEnabled(false);
    }

    /// <summary>任一场景(重新)加载完成:本标记仍存活时补一次点亮(等的是 Player 场景里的效果本体)。</summary>
    private void OnAnySceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (enabled && activeCount > 0)
            TrySetPassEnabled(true);
    }

    /// <summary>按材质名找到 TopDistortion 的 FullScreenCustomPass 并置 enabled。找到返回 true。</summary>
    private static bool TrySetPassEnabled(bool on)
    {
        bool found = false;
        foreach (var volume in FindObjectsOfType<CustomPassVolume>())
        {
            foreach (var pass in volume.customPasses)
            {
                var fullscreen = pass as FullScreenCustomPass;
                if (fullscreen == null || fullscreen.fullscreenPassMaterial == null) continue;
                if (fullscreen.fullscreenPassMaterial.name != k_TargetMaterialName) continue;
                fullscreen.enabled = on;
                found = true;
            }
        }
        if (!found)
            Debug.LogWarning($"[TopDistortionLevel] 未找到材质 {k_TargetMaterialName} 的 FullScreenCustomPass," +
                             $"效果未{(on ? "启用" : "关闭")}(开发者直玩缺 Player 场景等属正常;否则请确认 Player 场景 Volume 上挂好了该 Custom Pass)");
        return found;
    }
}
