using TMPro;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

/// <summary>
/// One of the 6 roster slots. Each slot needs its OWN GameObject (sync mode: Manual).
///
/// Synced data: who occupies the slot, and whether that player is alive.
/// The occupant owns the slot object, so only they write their own alive flag.
/// A slot whose occupant is no longer in the instance counts as free.
///
/// Don't call Claim/Release/SetAlive from other scripts - go through PlayerRoster.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class PlayerSlot : UdonSharpBehaviour
{
    [Tooltip("Leave empty if this slot is a child of the PlayerRoster object.")]
    [SerializeField] private PlayerRoster roster;
    [Tooltip("Optional: shows the occupant's name on your roster canvas.")]
    [SerializeField] private TextMeshProUGUI label;
    [SerializeField] private string emptyText = "- empty -";

    [UdonSynced] private int _occupantId = -1;
    [UdonSynced] private bool _alive = true;

    private void Start()
    {
        if (roster == null) roster = GetComponentInParent<PlayerRoster>();
        if (roster == null) Debug.LogWarning("[PlayerSlot] " + gameObject.name + ": roster NOT assigned");
        RefreshLabel();
    }

    // ---------- Read ----------

    public int GetRawOccupantId()
    {
        return _occupantId;
    }

    /// <summary>The occupant, or null if the slot is empty or the occupant already left.</summary>
    public VRCPlayerApi GetOccupant()
    {
        if (_occupantId < 0) return null;
        VRCPlayerApi player = VRCPlayerApi.GetPlayerById(_occupantId);
        return Utilities.IsValid(player) ? player : null;
    }

    public bool IsFree()
    {
        return GetOccupant() == null;
    }

    public bool IsOccupiedBy(VRCPlayerApi player)
    {
        return Utilities.IsValid(player) && _occupantId == player.playerId;
    }

    public bool IsAlive()
    {
        return _alive;
    }

    // ---------- Write (used by PlayerRoster) ----------

    public void Claim(VRCPlayerApi player)
    {
        TakeOwnership();
        _occupantId = player.playerId;
        _alive = true;
        Commit();
    }

    public void Release()
    {
        TakeOwnership();
        _occupantId = -1;
        _alive = true;
        Commit();
    }

    public void SetAlive(bool alive)
    {
        if (_alive == alive) return;
        TakeOwnership();
        _alive = alive;
        Commit();
    }

    private void TakeOwnership()
    {
        if (!Networking.IsOwner(gameObject)) Networking.SetOwner(Networking.LocalPlayer, gameObject);
    }

    private void Commit()
    {
        RequestSerialization();
        Changed(); // the owner does not get OnDeserialization
    }

    public override void OnDeserialization()
    {
        Changed();
    }

    private void Changed()
    {
        RefreshLabel();
        if (roster != null) roster.OnSlotChanged();
    }

    public void RefreshLabel()
    {
        if (label == null) return;

        VRCPlayerApi player = GetOccupant();
        if (player == null) label.text = emptyText;
        else label.text = _alive ? player.displayName : player.displayName + " (dead)";
    }
}
