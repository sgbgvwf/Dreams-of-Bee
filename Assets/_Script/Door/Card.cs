using UnityEngine;

/// <summary>
/// Marks a GameObject as a door card: when it enters a CardReader's trigger
/// zone, the swipe succeeds and the reader opens its door.
/// Also an Interactable of type Pickup, so the player can pick it up and
/// carry it to a reader (interaction flow: interactable first, then type).
/// Setup: attach this to the card, add a Rigidbody (isKinematic is fine) so
/// trigger events fire, and a Collider (the trigger flag is optional on the
/// card - the reader's zone does the detecting).
/// Attach to the card. Play Mode only.
/// </summary>
public class Card : Interactable
{
    [SerializeField, Tooltip("归属关卡索引（0 起，与 LevelTransitionManager 的关卡列表一致；-1=不校验）。防止上一关的卡刷开下一关的门")]
    private int levelIndex = -1;

    /// <summary>卡片所属关卡索引（-1 = 未配置，不校验）。</summary>
    public int LevelIndex => levelIndex;

    /// <summary>卡片的交互类型恒为拾取。</summary>
    public override InteractionType Type => InteractionType.Pickup;

    // Marker component: the reader detects cards by this component.
}
