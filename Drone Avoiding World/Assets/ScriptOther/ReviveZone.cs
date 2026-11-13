using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

/// <summary>
/// A dead player who walks into this trigger comes back to life. No sync (sync mode: None).
///
/// SETUP
///  - Put this on an object with a Collider set to "Is Trigger" (box, sphere...), on a layer that collides with Player.
///  - Assign the LocalPlayerState from the scene.
///  - spawnPoint: where the revived player appears (for example Point A). Empty = they come back where they stand.
///  - Keep the cemetery teleport spot OUTSIDE this trigger: teleporting into a trigger counts as entering it,
///    so the player would be revived the moment they die.
///
/// OnPlayerTriggerEnter runs on EVERY client for EVERY player, so it only acts for the local player.
/// Alive players and observers walking through do nothing (LocalPlayerState.Revive ignores them).
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class ReviveZone : UdonSharpBehaviour
{
    [SerializeField] private LocalPlayerState localState;
    [Tooltip("Where the revived player appears after touching the trigger. Empty = they come back where they stand.")]
    [SerializeField] private Transform spawnPoint;

    public override void OnPlayerTriggerEnter(VRCPlayerApi player)
    {
        if (!Utilities.IsValid(player) || !player.isLocal) return;
        if (localState == null) return;

        localState.Revive(spawnPoint);
    }
}
