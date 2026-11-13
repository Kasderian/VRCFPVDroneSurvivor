using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.Udon;

/// <summary>
/// Script behind the difficulty canvas. Local only (no sync) - GameManager does the syncing.
///
/// Udon buttons can't pass arguments, so there is one public method per button.
/// On each Button: OnClick() -> drag in this object -> UdonBehaviour.SendCustomEvent (string)
///   "SetDifficulty0" ... "SetDifficulty5"   (0 = stop game)
///   "ClaimHost"                              (take over as Game Host)
///
/// All UI references are optional - leave out what you don't have.
/// Add this object to GameManager's "listeners" so it refreshes automatically.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class DifficultyUI : UdonSharpBehaviour
{
    [SerializeField] private GameManager gameManager;

    [Header("Optional UI")]
    [SerializeField] private TextMeshProUGUI difficultyLabel;
    [SerializeField] private TextMeshProUGUI hostLabel;
    [Tooltip("Index = difficulty (0-5). Greyed out for everyone except the Game Host.")]
    [SerializeField] private Button[] difficultyButtons;
    [Tooltip("Greyed out while you already are the Game Host.")]
    [SerializeField] private Button claimHostButton;

    private void Start()
    {
        Refresh();
    }

    // ---------- Called by the canvas buttons ----------

    public void SetDifficulty0() { Debug.Log("[DifficultyUI] SetDifficulty0 pressed"); gameManager.SetDifficulty(0); }
    public void SetDifficulty1() { Debug.Log("[DifficultyUI] SetDifficulty1 pressed"); gameManager.SetDifficulty(1); }
    public void SetDifficulty2() { Debug.Log("[DifficultyUI] SetDifficulty2 pressed"); gameManager.SetDifficulty(2); }
    public void SetDifficulty3() { Debug.Log("[DifficultyUI] SetDifficulty3 pressed"); gameManager.SetDifficulty(3); }
    public void SetDifficulty4() { Debug.Log("[DifficultyUI] SetDifficulty4 pressed"); gameManager.SetDifficulty(4); }
    public void SetDifficulty5() { Debug.Log("[DifficultyUI] SetDifficulty5 pressed"); gameManager.SetDifficulty(5); }

    public void ClaimHost() { Debug.Log("[DifficultyUI] ClaimHost pressed"); gameManager.ClaimHost(); }

    // ---------- Called by GameManager ----------

    public void OnGameStateChanged()
    {
        Refresh();
    }

    private void Refresh()
    {
        int difficulty = gameManager.GetDifficulty();
        bool isHost = gameManager.IsGameHost();

        if (difficultyLabel != null)
        {
            difficultyLabel.text = difficulty == 0 ? "Game stopped" : "Difficulty " + difficulty.ToString() + " / 5";
        }

        if (hostLabel != null)
        {
            VRCPlayerApi host = gameManager.GetGameHost();
            hostLabel.text = Utilities.IsValid(host) ? "Game Host: " + host.displayName : "Game Host: -";
        }

        if (difficultyButtons != null)
        {
            for (int i = 0; i < difficultyButtons.Length; i++)
            {
                if (difficultyButtons[i] != null) difficultyButtons[i].interactable = isHost;
            }
        }

        if (claimHostButton != null) claimHostButton.interactable = !isHost;
    }
}
