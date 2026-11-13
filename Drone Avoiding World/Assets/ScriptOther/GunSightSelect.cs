using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

/// <summary>
/// One of the stationary sight cubes. Shoot it to change the sight on the gun you are holding.
/// Put it on the cube itself (a NON-trigger collider: BallisticManager ignores triggers), one cube per sight.
/// No sync (sync mode: None): the change is local, the gun (TwoHandGun) syncs the chosen sight.
///
/// BallisticManager calls OnBulletHit() on the nearest UdonSharpBehaviour above the collider it hit, on the SHOOTER's
/// client only. So the gun to change is always the local player's held gun.
///
/// SETUP
///  - sightIndex: 0 = iron, 1 = holographic, 2 = scope (same order as the 'Sights' list on the gun).
///  - The cube's layer must be ticked in BallisticManager's Hit Mask.
///  - Feedback (optional): a material that shows for flashSeconds when the sight was changed.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class GunSightSelect : UdonSharpBehaviour
{
    [Tooltip("Which sight this cube selects: 0 = iron, 1 = holographic, 2 = scope.")]
    public int sightIndex = 0;
    [Tooltip("Further hits are ignored for this long (full auto, shotgun pellets).")]
    public float hitLockSeconds = 0.5f;

    [Header("Feedback (optional)")]
    public Renderer cubeRenderer;      // found on this object if left empty
    public Material hitMaterial;       // empty = no flash
    public float flashSeconds = 0.5f;

    private Material originalMaterial;
    private float lockUntil;
    private float restoreTime = -1f;

    void Start()
    {
        if (cubeRenderer == null) cubeRenderer = GetComponent<Renderer>();
        if (cubeRenderer != null) originalMaterial = cubeRenderer.sharedMaterial;
    }

    // Called LOCALLY on the shooter's client by BallisticManager after its raycast hit this cube.
    public void OnBulletHit()
    {
        if (Time.time < lockUntil) return;
        lockUntil = Time.time + hitLockSeconds;

        TwoHandGun gun = FindHeldGun();
        if (gun == null) return;
        if (!gun.SetSight(sightIndex)) return;   // refused: not the holder, or this gun has no such sight

        Flash();
    }

    // The TwoHandGun in either hand. The foregrip handle is a pickup too, but it has no TwoHandGun on it,
    // so it is skipped and the other hand (holding the gun itself) is found.
    TwoHandGun FindHeldGun()
    {
        VRCPlayerApi lp = Networking.LocalPlayer;
        if (!Utilities.IsValid(lp)) return null;

        VRC_Pickup right = lp.GetPickupInHand(VRC_Pickup.PickupHand.Right);
        if (Utilities.IsValid(right))
        {
            TwoHandGun gRight = right.GetComponent<TwoHandGun>();
            if (gRight != null) return gRight;
        }

        VRC_Pickup left = lp.GetPickupInHand(VRC_Pickup.PickupHand.Left);
        if (Utilities.IsValid(left))
        {
            TwoHandGun gLeft = left.GetComponent<TwoHandGun>();
            if (gLeft != null) return gLeft;
        }

        return null;
    }

    void Flash()
    {
        if (cubeRenderer == null || hitMaterial == null) return;
        cubeRenderer.sharedMaterial = hitMaterial;
        restoreTime = Time.time + flashSeconds;
    }

    void Update()
    {
        if (restoreTime > 0f && Time.time >= restoreTime)
        {
            cubeRenderer.sharedMaterial = originalMaterial;
            restoreTime = -1f;
        }
    }
}
