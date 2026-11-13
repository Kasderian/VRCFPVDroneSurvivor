using UdonSharp;
using UnityEngine;

/// <summary>
/// Hit relay of ONE Spotter. Put it on the SAME object as the Spotter's (non-trigger) collider
/// ("SpotterBody" in the prefab, layer OpFor).
///
/// BallisticManager calls OnBulletHit() on the nearest UdonSharpBehaviour above the collider it hit,
/// on the shooter's client only. This script just forwards that to the SpotterManager, which decides
/// what to network. Nothing is synced here (sync mode: None).
///
/// 'manager' and 'index' are filled in by the SpotterManager at Start, so the prefab needs no scene references.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SpotterHit : UdonSharpBehaviour
{
    [HideInInspector] public SpotterManager manager;
    [HideInInspector] public int index;

    public void OnBulletHit()
    {
        if (manager != null) manager.ShotSpotter(index);
    }
}
