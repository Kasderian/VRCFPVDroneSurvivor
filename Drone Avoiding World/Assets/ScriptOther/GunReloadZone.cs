using UdonSharp;
using UnityEngine;

/// <summary>
/// Reload relay. Put it on the SAME object as the magazine (loading port) collider, a child of Gun inner.
/// Make that Box Collider a TRIGGER, so it does not change the gun's physics and bullets ignore it.
///
/// VR: clicking the TRIGGER (not grip) while pointing at the collider calls TryReload() on the gun.
/// The collider is only interactive while the local player holds the gun (TwoHandGun switches it).
/// The gun itself checks that you are the holder, so a stray click on a gun lying around does nothing.
/// Nothing is synced here (sync mode: None).
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class GunReloadZone : UdonSharpBehaviour
{
    [Tooltip("The TwoHandGun on the Gun root object.")]
    public TwoHandGun gun;

    void Start()
    {
        DisableInteractive = true;
    }

    // TwoHandGun calls this when the local player picks the gun up / drops it.
    public void SetUsable(bool usable)
    {
        DisableInteractive = !usable;
    }

    public override void Interact()
    {
        if (gun != null) gun.TryReload();
    }
}
