using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

/// <summary>
/// Roster of playing players (max = number of slots, assign 6 PlayerSlots).
/// Local only (no sync) - the PlayerSlots do the syncing.
///
/// Join/Leave button: OnClick() -> this object -> UdonBehaviour.SendCustomEvent (string) "ToggleJoin"
/// (or "Join" / "Leave" if you want separate buttons).
///
/// Drones / VisibilityChecker / OpForIntel should only target players returned by GetTargets().
/// Observers (not in a slot) never show up there.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class PlayerRoster : UdonSharpBehaviour
{
    [SerializeField] private PlayerSlot[] slots;
    [Tooltip("The LocalPlayerState on this client; it is told whenever the roster changes.")]
    [SerializeField] private LocalPlayerState localState;

    [Header("Optional UI")]
    [Tooltip("ONE text that lists all 6 slots (name, or \"-\" if empty, \"(dead)\" if killed).")]
    [SerializeField] private TextMeshProUGUI playerListLabel;
    [SerializeField] private TextMeshProUGUI joinButtonLabel; // "Join" / "Leave" / "Full"
    [SerializeField] private TextMeshProUGUI countLabel;      // "Players 3 / 6"
    [SerializeField] private Button joinButton;               // greyed out when the roster is full

    // True after pressing Join until pressing Leave (or the roster turns out to be full).
    // Lets us retry automatically if two players grabbed the same slot at the same moment.
    private bool _wantsToPlay;

    private void Start()
    {
        RefreshSlotLabels();
        RefreshAll();
    }

    // ---------- Called by the canvas button ----------

    public void ToggleJoin()
    {
        Debug.Log("[PlayerRoster] ToggleJoin pressed, slots assigned: " + slots.Length.ToString());
        if (IsLocalPlaying()) Leave();
        else Join();
    }

    public void Join()
    {
        if (IsLocalPlaying()) return;
        _wantsToPlay = true;
        TryClaimFreeSlot();
    }

    public void Leave()
    {
        _wantsToPlay = false;
        PlayerSlot mine = GetLocalSlot();
        if (mine != null) mine.Release(); // notifies the roster
        else RefreshAll();
    }

    // ---------- Queries (safe to call from any script, any client) ----------

    public int MaxPlayers()
    {
        return slots.Length;
    }

    public PlayerSlot GetSlotOf(VRCPlayerApi player)
    {
        if (!Utilities.IsValid(player)) return null;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].IsOccupiedBy(player)) return slots[i];
        }
        return null;
    }

    public PlayerSlot GetLocalSlot()
    {
        return GetSlotOf(Networking.LocalPlayer);
    }

    public bool IsPlaying(VRCPlayerApi player)
    {
        return GetSlotOf(player) != null;
    }

    public bool IsLocalPlaying()
    {
        return GetLocalSlot() != null;
    }

    /// <summary>False for observers.</summary>
    public bool IsAlive(VRCPlayerApi player)
    {
        PlayerSlot slot = GetSlotOf(player);
        return slot != null && slot.IsAlive();
    }

    public bool IsLocalAlive()
    {
        return IsAlive(Networking.LocalPlayer);
    }

    public int GetPlayingCount()
    {
        int count = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            if (!slots[i].IsFree()) count++;
        }
        return count;
    }

    /// <summary>
    /// Fills "buffer" with the playing AND alive players (what drones may target). Returns how many.
    /// Pass a preallocated VRCPlayerApi[6] to avoid allocating every call.
    /// </summary>
    public int GetTargets(VRCPlayerApi[] buffer)
    {
        return Collect(buffer, true);
    }

    /// <summary>Same, but includes dead players that are still in the roster.</summary>
    public int GetPlayingPlayers(VRCPlayerApi[] buffer)
    {
        return Collect(buffer, false);
    }

    private int Collect(VRCPlayerApi[] buffer, bool aliveOnly)
    {
        int n = 0;
        for (int i = 0; i < slots.Length && n < buffer.Length; i++)
        {
            VRCPlayerApi player = slots[i].GetOccupant();
            if (!Utilities.IsValid(player)) continue;
            if (aliveOnly && !slots[i].IsAlive()) continue;
            buffer[n] = player;
            n++;
        }
        return n;
    }

    // ---------- Used by LocalPlayerState ----------

    public void SetLocalAlive(bool alive)
    {
        PlayerSlot mine = GetLocalSlot();
        if (mine != null) mine.SetAlive(alive);
    }

    // ---------- Joining ----------

    private PlayerSlot FindFreeSlot()
    {
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].IsFree()) return slots[i];
        }
        return null;
    }

    private void TryClaimFreeSlot()
    {
        VRCPlayerApi local = Networking.LocalPlayer;
        if (!Utilities.IsValid(local)) return;

        PlayerSlot free = FindFreeSlot();
        if (free == null)
        {
            _wantsToPlay = false; // roster is full
            RefreshAll();
            return;
        }

        free.Claim(local); // notifies the roster, which refreshes everything
    }

    // ---------- Callbacks ----------

    /// <summary>Called by a PlayerSlot whenever its occupant or alive flag changed.</summary>
    public void OnSlotChanged()
    {
        // We pressed Join but aren't in a slot: someone else grabbed ours at the same moment
        // (or it got cleared). Take another free slot.
        if (_wantsToPlay && !IsLocalPlaying()) TryClaimFreeSlot();

        RefreshAll();
    }

    public override void OnPlayerJoined(VRCPlayerApi player)
    {
        // Late joiners can receive slot data before they know the players - redraw names.
        RefreshSlotLabels();
        RefreshAll();
    }

    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        // The master frees the slots of players who are gone, so the data is clean for late joiners.
        // (Everyone already treats such slots as free, see PlayerSlot.GetOccupant.)
        if (Networking.IsMaster)
        {
            int leftId = Utilities.IsValid(player) ? player.playerId : -2;
            for (int i = 0; i < slots.Length; i++)
            {
                int raw = slots[i].GetRawOccupantId();
                if (raw < 0) continue;
                if (raw == leftId || slots[i].GetOccupant() == null) slots[i].Release();
            }
        }

        RefreshSlotLabels();
        RefreshAll();
    }

    // ---------- UI ----------

    private void RefreshSlotLabels()
    {
        for (int i = 0; i < slots.Length; i++) slots[i].RefreshLabel();
    }

    private string BuildPlayerList()
    {
        string list = "";
        for (int i = 0; i < slots.Length; i++)
        {
            VRCPlayerApi player = slots[i].GetOccupant();
            string line = (i + 1).ToString() + ". ";

            if (player == null) line += "-";
            else if (slots[i].IsAlive()) line += player.displayName;
            else line += player.displayName + " (dead)";

            list += line;
            if (i < slots.Length - 1) list += "\n";
        }
        return list;
    }

    private void RefreshAll()
    {
        int count = GetPlayingCount();
        bool playing = IsLocalPlaying();
        bool full = count >= slots.Length;

        if (playerListLabel != null) playerListLabel.text = BuildPlayerList();
        if (countLabel != null) countLabel.text = "Players " + count.ToString() + " / " + slots.Length.ToString();
        if (joinButtonLabel != null) joinButtonLabel.text = playing ? "Leave" : (full ? "Full" : "Join");
        if (joinButton != null) joinButton.interactable = playing || !full;

        if (localState != null) localState.Refresh();
    }
}
