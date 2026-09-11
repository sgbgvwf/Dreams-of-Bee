using UnityEngine;

/// <summary>
/// 门面穿门触发器 —— 挂在【门面平面（贴 RenderTexture 的那张 Quad）】上。
///
/// 是什么：玩家碰撞体进入本物体的触发区，立刻叫同一扇门的 PortalDoor 执行传送。
/// 把"穿门"从数学推算（玩家身体中心在门平面哪一侧）换成物理触发：碰到就算数 ——
/// 贴地飞 / 贴边飞都传，也不会先扎进画面里再被拽走。
///
/// 放哪：门面平面（门洞处那张 Quad）上，和它的 Is Trigger 碰撞体同一个物体。
///
/// 绑什么：portalDoor 槽拖【同一预制体里出口锚点上的 PortalDoor】（门面与出口锚点同属
/// 移动门.prefab，可以直接拖）；触发体自己配 Is Trigger。
///
/// 常见坑：
///   1. 触发体要留厚度（0.2~0.4 左右）：零厚度的盒子在 PhysX 里是退化几何，可能压根不产生
///      重叠事件；薄板 + 快速飞行还可能一帧直接穿过去（Awake 里对 BoxCollider 会提示一次）。
///   2. 触发体摆在哪 = 传送发生在哪：想让玩家"还没贴到画面就被传走"就把它朝玩家来的一侧挪一点，
///      朝另一侧挪就是穿进去一点再传 —— 手感全靠这个位置，不是在代码里调。
///   3. 门没开时玩家先撞到的是门板，碰不到门面；PortalDoor 另有 active 门闩，未激活一律不传送。
///   4. 只有玩家本体（身上带 Rigidbody 的物体）算数：手里举着的钥匙 / 道具自带 Rigidbody，
///      碰到门面不会把人提前传走（身份判定在 PortalDoor.OnFaceTouched 里做）。
/// </summary>
public class PortalCrossTrigger : MonoBehaviour
{
    [SerializeField, Tooltip("同一扇门的 PortalDoor（预制体里出口锚点上的那个）：玩家进入触发区后由它执行传送")]
    private PortalDoor portalDoor;

    private bool warnedMissingDoor;

    private void Awake()
    {
        // 零厚度触发体是 PhysX 的退化几何，可能不产生重叠事件（见头注释坑 1）
        if (TryGetComponent<BoxCollider>(out var box) && box.isTrigger && Mathf.Abs(box.size.z) < 0.02f)
            Debug.LogWarning($"[PortalCrossTrigger] 触发体几乎零厚（z={box.size.z:F3}）：PhysX 可能不产生重叠事件，建议给 0.2~0.4 厚度", this);
    }

    private void OnTriggerEnter(Collider other)
    {
        if (portalDoor == null)
        {
            if (!warnedMissingDoor)
            {
                warnedMissingDoor = true;
                Debug.LogError($"[PortalCrossTrigger] 未指定 PortalDoor：{name}（请拖同一预制体出口锚点上的 PortalDoor）", this);
            }
            return;
        }

        portalDoor.OnFaceTouched(other);   // 身份过滤 / 激活门闩都由 PortalDoor 负责
    }
}
