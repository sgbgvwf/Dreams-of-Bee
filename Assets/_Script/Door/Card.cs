using UnityEngine;

/// <summary>
/// Marks a GameObject as a door card: when it enters a CardReader's trigger
/// zone, the swipe succeeds and the reader opens its door.
/// Setup: attach this to the card, add a Rigidbody (isKinematic is fine) so
/// trigger events fire, and a Collider (the trigger flag is optional on the
/// card - the reader's zone does the detecting).
/// Attach to the card. Play Mode only.
/// </summary>
public class Card : MonoBehaviour
{
    // Marker component: the reader detects cards by this component.
}
