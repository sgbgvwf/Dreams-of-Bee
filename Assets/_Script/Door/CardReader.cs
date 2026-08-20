using UnityEngine;

/// <summary>
/// A card reader: when a Card enters the reader's trigger zone, the swipe
/// succeeds and the assigned door opens. The reader only ever opens the door
/// - it never closes it; closing is left to other mechanisms (a button, a
/// player-operated switch, another script).
/// Setup: attach this script to the reader, add a Collider with "Is Trigger"
/// ticked, sized as the swipe range ("near the reader"), and drag the door's
/// SlidingDoor component into the door field. The card needs a Rigidbody for
/// trigger events to fire (see Card.cs).
/// Optionally drag the door's LightColorAlternator into successLight: on a
/// successful swipe it shows its second preset color (e.g. green) as visual
/// feedback. Auto-filled from the door's children when left empty.
/// Attach to the reader. Play Mode only.
/// </summary>
public class CardReader : MonoBehaviour
{
    [SerializeField, Tooltip("The door this reader opens (the SlidingDoor component on the door root).")]
    private SlidingDoor door;

    [SerializeField, Tooltip("Optional: a LightColorAlternator that shows its second preset color (e.g. green) on a successful swipe. Auto-filled from the door's children when empty.")]
    private LightColorAlternator successLight;

    private bool warnedMissingDoor;

    private void OnTriggerEnter(Collider other)
    {
        // Only a card triggers a swipe.
        if (other.GetComponent<Card>() == null) return;

        if (door == null)
        {
            if (!warnedMissingDoor)
            {
                warnedMissingDoor = true;
                Debug.LogWarning($"[CardReader] No door assigned on {name}. " +
                    "Drag the door's SlidingDoor component into the Inspector.");
            }
            return;
        }

        SwipeSucceeded();
    }

    private void SwipeSucceeded()
    {
        // Swiping only ever opens the door; it never closes it.
        door.OpenDoor();

        // Success feedback: show the lamp's second preset color (green).
        if (successLight == null && door != null)
            successLight = door.GetComponentInChildren<LightColorAlternator>(true);
        if (successLight != null) successLight.ShowColor(true);
    }
}
