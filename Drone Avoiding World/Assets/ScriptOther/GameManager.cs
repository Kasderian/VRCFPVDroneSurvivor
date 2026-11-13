using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

/// <summary>
/// Synced difficulty (0 = game stopped, 1-5 = running) + the "Game Host" role.
///
/// The Game Host IS the network owner of this GameObject:
///  - anyone can claim it at any time with ClaimHost()
///  - only the Game Host can change difficulty
///  - only the Game Host's client should run robot AI (check IsGameHost())
///  - if the Game Host leaves the world, VRChat hands ownership to another player automatically
///
/// Put this on its own GameObject. Sync mode: Manual.
/// Anything that needs to react (DifficultyUI, OpForIntel, spawners...) can be added to "listeners";
/// they receive the custom event "OnGameStateChanged" whenever difficulty or Game Host changes.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class GameManager : UdonSharpBehaviour
{
    public const int MaxDifficulty = 5;

    [SerializeField] private UdonSharpBehaviour[] listeners;

    [UdonSynced] private int _difficulty = 0;

    // ---------- Read ----------

    /// <summary>0 = game stopped, 1-5 = running.</summary>
    public int GetDifficulty()
    {
        return _difficulty;
    }

    public bool IsGameRunning()
    {
        return _difficulty > 0;
    }

    public bool IsGameHost()
    {
        return Networking.IsOwner(gameObject);
    }

    public VRCPlayerApi GetGameHost()
    {
        return Networking.GetOwner(gameObject);
    }

    // ---------- Write (called by DifficultyUI buttons) ----------

    /// <summary>Only works on the Game Host's client. Values are clamped to 0-5.</summary>
    public void SetDifficulty(int difficulty)
    {
        if (!IsGameHost())
        {
            Debug.Log("[GameManager] SetDifficulty ignored: not Game Host");
            return;
        }

        difficulty = Mathf.Clamp(difficulty, 0, MaxDifficulty);
        if (difficulty == _difficulty) return;

        _difficulty = difficulty;
        Debug.Log("[GameManager] difficulty set to " + difficulty.ToString() + ", listeners: " + (listeners == null ? 0 : listeners.Length).ToString());
        RequestSerialization();
        NotifyListeners(); // the owner does not get OnDeserialization, so notify locally
    }

    /// <summary>Anyone can take over as Game Host.</summary>
    public void ClaimHost()
    {
        if (IsGameHost()) return;
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
        NotifyListeners();
    }

    // ---------- Network callbacks ----------

    public override void OnDeserialization()
    {
        NotifyListeners(); // difficulty changed by the Game Host (or initial state for late joiners)
    }

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        NotifyListeners(); // Game Host changed
    }

    private void NotifyListeners()
    {
        if (listeners == null) return;
        for (int i = 0; i < listeners.Length; i++)
        {
            if (listeners[i] != null) listeners[i].SendCustomEvent("OnGameStateChanged");
        }
    }
}
