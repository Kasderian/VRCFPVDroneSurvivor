using UdonSharp;
using UnityEngine;

/// <summary>
/// The synced variables of ONE drone. Put this on an empty CHILD object of the drone prefab root
/// (sync mode Manual, set by the attribute below).
///
/// Why it is separate: VRC Object Sync (on the drone root) cannot share an object with a
/// Manual-sync Udon behaviour, so DroneChaser (NoVariableSync) keeps its variables here.
///
/// The drone owner writes these through DroneChaser and calls Push(). Everyone else receives them
/// and this script tells DroneChaser via the "OnSyncReceived" event.
/// The owner of THIS object must be the same player as the owner of the drone (DroneChaser takes care of that).
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DroneNetSync : UdonSharpBehaviour
{
    [Tooltip("The DroneChaser on the drone root object.")]
    public UdonSharpBehaviour drone;

    [HideInInspector, UdonSynced] public int syncState;
    [HideInInspector, UdonSynced] public float syncAlt;
    [HideInInspector, UdonSynced] public float syncYaw;
    [HideInInspector, UdonSynced] public Vector3 syncTravelDest;
    [HideInInspector, UdonSynced] public int syncExplosionSeq;
    [HideInInspector, UdonSynced] public Vector3 syncExplosionPos;
    [HideInInspector, UdonSynced] public Vector3 syncDiveCommit;
    [HideInInspector, UdonSynced] public Vector3 syncDiveDir;
    [HideInInspector, UdonSynced] public float syncDiveEndY;
    [HideInInspector, UdonSynced] public float syncDiveSlope;

    public void Push()
    {
        RequestSerialization();
    }

    public override void OnDeserialization()
    {
        if (drone != null) drone.SendCustomEvent("OnSyncReceived");
    }
}
