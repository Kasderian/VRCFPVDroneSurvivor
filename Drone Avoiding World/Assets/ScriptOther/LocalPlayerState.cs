using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

/// <summary>
/// State of the LOCAL player on this client. One instance in the scene, no sync
/// (sync mode: None) - every client has its own copy for its own player.
///
///  isPlaying : this player joined the game (is in a roster slot). Observers: false.
///  alive     : false after Kill(). Only meaningful while playing (observers read true).
///
/// "alive" is mirrored into the player's PlayerSlot so the Game Host's robots can see who is dead.
/// Every change of isPlaying or alive resets the stamina system (speeds + HUD).
///
/// Explosion / bullet scripts call Kill() on the local client when THIS player is hit:
///     localState.Kill();
///
/// COMING BACK
///  Two ways, both end in Revive(spawn):
///   - the timer: autoReviveSeconds after dying (0 = off)
///   - a ReviveZone trigger collider the dead player walks into
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class LocalPlayerState : UdonSharpBehaviour
{
    [SerializeField] private PlayerRoster roster;
    [Tooltip("Empty GameObject where killed players are teleported. Keep it OUTSIDE any ReviveZone.")]
    [SerializeField] private Transform cemetery;

    [Header("Coming back to life")]
    [Tooltip("A dead player comes back by themselves after this many seconds. 0 = never (only a ReviveZone brings them back).")]
    [SerializeField] private float autoReviveSeconds = 30f;
    [Tooltip("Where the timer puts the player after setting them as alive (for example Point A). Empty = they come back where they are.")]
    [SerializeField] private Transform autoReviveSpawn;

    [Header("Stamina Script")]
    [Tooltip("Receives ResetStamina() whenever isPlaying or alive changes.")]
    [SerializeField] private StaminaSystem staminaSystem;

    [HideInInspector] public bool isPlaying = false;
    [HideInInspector] public bool alive = true;

    private bool _ready;

    // what the stamina system was last told (so Refresh() can run often without resetting stamina)
    private bool _applied;
    private bool _appliedPlaying;
    private bool _appliedAlive = true;

    private float _reviveAt = -1f;   // Time.time when the timer brings this player back; -1 = no timer running

    private void Start()
    {
        // Wait a couple of frames so the stamina script has run its own Start() before we touch it.
        SendCustomEventDelayedFrames(nameof(Init), 2);
    }

    public void Init()
    {
        _ready = true;
        Refresh();
    }

    private void Update()
    {
        if (_reviveAt < 0f) return;

        // left the game, or already alive again (a ReviveZone got there first): the timer is void
        if (!isPlaying || alive)
        {
            _reviveAt = -1f;
            return;
        }

        if (Time.time >= _reviveAt) Revive(autoReviveSpawn);
    }

    /// <summary>Re-reads isPlaying/alive from the roster. PlayerRoster calls this on every change.</summary>
    public void Refresh()
    {
        if (!_ready) return;

        bool playing = Utilities.IsValid(Networking.LocalPlayer) && roster.IsLocalPlaying();
        isPlaying = playing;
        alive = !playing || roster.IsLocalAlive();

        ApplyStamina();
    }

    /// <summary>True while a robot or bomb may hurt this player.</summary>
    public bool IsTargetable()
    {
        return isPlaying && alive;
    }

    /// <summary>Seconds until the timer brings this player back. -1 when no timer is running (alive, observer or timer off).</summary>
    public float GetReviveSecondsLeft()
    {
        if (_reviveAt < 0f) return -1f;
        return Mathf.Max(_reviveAt - Time.time, 0f);
    }

    /// <summary>Player was hit: stamina reset, teleport to the cemetery, start the revive timer. Ignored for observers and the dead.</summary>
    public void Kill()
    {
        if (!isPlaying || !alive) return;

        alive = false;
        _reviveAt = (autoReviveSeconds > 0f) ? Time.time + autoReviveSeconds : -1f;
        roster.SetLocalAlive(false); // synced, so the Game Host's drones stop targeting this player
        ApplyStamina();

        VRCPlayerApi local = Networking.LocalPlayer;
        if (cemetery != null && Utilities.IsValid(local))
        {
            local.TeleportTo(cemetery.position, cemetery.rotation);
        }
    }

    /// <summary>
    /// Brings a dead player back. spawn = where they appear (null = they stay where they are).
    /// Called by the timer above and by ReviveZone. Ignored for observers and for players who are alive.
    /// </summary>
    public void Revive(Transform spawn)
    {
        if (!isPlaying || alive) return;

        alive = true;
        _reviveAt = -1f;
        roster.SetLocalAlive(true); // synced: the robots may target this player again
        ApplyStamina();

        VRCPlayerApi local = Networking.LocalPlayer;
        if (spawn != null && Utilities.IsValid(local))
        {
            local.TeleportTo(spawn.position, spawn.rotation);
        }
    }

    // Refresh() runs on every roster change (any player joining, leaving, dying), so the stamina system is only told
    // when isPlaying or alive actually changed. Comparing only (isPlaying && alive) missed "dead -> left the game".
    private void ApplyStamina()
    {
        if (_applied && isPlaying == _appliedPlaying && alive == _appliedAlive) return;
        _applied = true;
        _appliedPlaying = isPlaying;
        _appliedAlive = alive;

        if (staminaSystem != null) staminaSystem.ResetStamina();
    }
}
