
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

/// Put this on an interactable object. Teleports the player to the target when interacted with.
public class TeleportOnInteract : UdonSharpBehaviour
{
    public Transform teleportTarget;

    public override void Interact()
    {
        Networking.LocalPlayer.TeleportTo(
        teleportTarget.position,
        teleportTarget.rotation
        );
    }
}
