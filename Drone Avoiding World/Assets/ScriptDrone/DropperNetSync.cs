using UdonSharp;
using UnityEngine;

/// <summary>
/// The synced variables of ONE Dropper. Put this on an empty CHILD object of the Dropper prefab root
/// (sync mode Manual, set by the attribute below). Same idea as DroneNetSync, with the bomb data added.
///
/// Why it is separate: VRC Object Sync (on the Dropper root) cannot share an object with a
/// Manual-sync Udon behaviour, so DroneDropper (NoVariableSync) keeps its variables here.
///
/// The Dropper owner writes these through DroneDropper and calls Push(). Everyone else receives them
/// and this script tells DroneDropper via the "OnSyncReceived" event.
/// The owner of THIS object must be the same player as the owner of the Dropper (DroneDropper takes care of that).
///
/// Bombs: a bomb is announced by raising syncBombSeq and filling syncBombPos / syncBombVel / syncBombTime.
/// Every client then simulates the fall by itself (same start values, same physics, so the same result).
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DropperNetSync : UdonSharpBehaviour
{
    [Tooltip("The DroneDropper on the Dropper root object.")]
    public UdonSharpBehaviour drone;

    [HideInInspector, UdonSynced] public int syncState;
    [HideInInspector, UdonSynced] public float syncAlt;
    [HideInInspector, UdonSynced] public float syncYaw;
    [HideInInspector, UdonSynced] public Vector3 syncTravelDest;
    [HideInInspector, UdonSynced] public int syncBombsLeft;
    [HideInInspector, UdonSynced] public int syncExplosionSeq;
    [HideInInspector, UdonSynced] public Vector3 syncExplosionPos;
    [HideInInspector, UdonSynced] public int syncBombSeq;
    [HideInInspector, UdonSynced] public Vector3 syncBombPos;
    [HideInInspector, UdonSynced] public Vector3 syncBombVel;
    [HideInInspector, UdonSynced] public double syncBombTime;

    public void Push()
    {
        RequestSerialization();
    }

    public override void OnDeserialization()
    {
        if (drone != null) drone.SendCustomEvent("OnSyncReceived");
    }
}
