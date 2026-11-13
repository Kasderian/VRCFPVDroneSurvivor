
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using TMPro;
using System;

public class StaminaSystem : UdonSharpBehaviour
{
    [Header("Stamina")]
    [SerializeField] private float maxStamina = 200f;
    [SerializeField] private float currentStamina = 200f;

    [Header("Movement Speeds")]
    [SerializeField] private float walkingSpeed = 2f;
    [SerializeField] private float runningSpeed = 4f;
    [Tooltip("Speeds for players who are NOT in the game.")]
    [SerializeField] private float observerWalkSpeed = 2f;
    [SerializeField] private float observerRunSpeed = 10f;

    [Header("Running Drain")]
    [SerializeField] private float maximumDrainPerSecond = 15f;
    [SerializeField] private float drainExponent = 2.5f;

    [Header("Walking Recovery")]
    [SerializeField] private float maximumRecoveryPerSecond = 10f;
    [SerializeField] private float walkingRecoveryMultiplier = 0.5f;
    [SerializeField] private float recoveryExponent = 1.5f;
    [SerializeField] private float walkingSpeedTolerance = 0.01f;

    [Header("Exhaustion")]
    [SerializeField] private float minimumStaminaToRun = 5f;
    [SerializeField] private float minimumStaminaToRecover = 20f;
    private bool movementSpeedRestricted;

    [Header("References")]
    [SerializeField] private TMP_Text StaminaHUD;
    [SerializeField] private bool showStaminaHUD = true;
    [SerializeField] private LocalPlayerState localPlayerState;

    [Header("HUD placement (world space canvas)")]
    [Tooltip("Root of the World Space canvas. It is moved to your head every frame.")]
    [SerializeField] private Transform hudRoot;
    [Tooltip("Head-local offset: x right, y up, z forward (metres).")]
    [SerializeField] private Vector3 hudOffset = new Vector3(0f, -0.18f, 0.6f);
    [Tooltip("Higher = tighter follow. 0 = locked rigidly to the head.")]
    [SerializeField] private float hudFollowSpeed = 8f;

    private VRCPlayerApi localPlayer;
    private float currentSpeed;
    private int staminaDelta = 0;
    private bool isExhausted;

    private void Start()
    {
        localPlayer = Networking.LocalPlayer;
        currentStamina = maxStamina;
        isExhausted = false;

        ToggleMaxSpeed();
    }

    private void Update()
    {
        if (!Utilities.IsValid(localPlayer))
        {
            return;
        }

        if (localPlayerState != null && localPlayerState.isPlaying)
        {
            UpdateSpeed();
            UpdateStamina();
            UpdateMovementRestriction();
            UpdateDebugDisplay();
        }
    }

    private void UpdateSpeed()
    {
        Vector3 velocity = localPlayer.GetVelocity();

        // Ignore vertical velocity so jumping/falling does not affect stamina.
        Vector3 horizontalVelocity = new Vector3(
            velocity.x,
            0f,
            velocity.z
        );

        currentSpeed = horizontalVelocity.magnitude;
    }

    private void UpdateStamina()
    {
        //currentSpeed is floating so it bounces around 2f. Tolerance just to be sure.
        if (currentSpeed <= walkingSpeed + walkingSpeedTolerance)
        {
            RecoverStamina();
        }
        else
        {
            DrainStamina();
        }

        currentStamina = Mathf.Clamp(currentStamina, 0f, maxStamina);

        // Exhaustion state.
        if (currentStamina < minimumStaminaToRun)
        {
            isExhausted = true;
        }
        else if (currentStamina > minimumStaminaToRecover)
        {
            isExhausted = false;
        }

    }

    private void DrainStamina()
    {
        // Convert speed between walking and running into a 0-1 value.
        float normalizedSpeed = Mathf.InverseLerp(
            walkingSpeed,
            runningSpeed,
            currentSpeed
        );

        // Nonlinear drain.
        float drainMultiplier = Mathf.Pow(
            normalizedSpeed,
            drainExponent
        );

        float drainAmount =
            maximumDrainPerSecond *
            drainMultiplier *
            Time.deltaTime;

        currentStamina -= drainAmount;
        staminaDelta = -(int)(drainAmount * 100);
    }

    private void RecoverStamina()
    {
        // Convert walking speed into a 0-1 value.
        // 0 = standing still
        // 1 = walking speed
        float normalizedWalkingSpeed = Mathf.InverseLerp(
            0f,
            walkingSpeed,
            currentSpeed
        );

        // Standing still recovers fastest.
        // Walking recovers at the configured minimum percentage.
        float recoveryMultiplier = Mathf.Lerp(
            walkingRecoveryMultiplier,
            1f,
            1f - Mathf.Pow(
                normalizedWalkingSpeed,
                recoveryExponent
            )
        );

        float recoveryAmount =
            maximumRecoveryPerSecond *
            recoveryMultiplier *
            Time.deltaTime;

        currentStamina += recoveryAmount;
        staminaDelta = (int)(recoveryAmount * 100);
    }

    private void UpdateMovementRestriction()
    {
        if (isExhausted && !movementSpeedRestricted)
        {
            localPlayer.SetRunSpeed(walkingSpeed);
            movementSpeedRestricted = true;
        }
        else if (!isExhausted && movementSpeedRestricted)
        {
            localPlayer.SetRunSpeed(runningSpeed);
            movementSpeedRestricted = false;
        }

    }

    private void UpdateDebugDisplay()
    {
        if (StaminaHUD == null) return;
        if (!showStaminaHUD || (localPlayerState != null && !localPlayerState.isPlaying))
        {
            StaminaHUD.text = "";
            return;
        }
        StaminaHUD.text =
            currentStamina.ToString("0.0") +
            " / " + maxStamina.ToString("0.0") +
            "\n" + staminaDelta +
            "\n" + currentSpeed.ToString("0.00");


    }

    private void ToggleMaxSpeed()
    {
        if (localPlayerState == null) { Debug.LogWarning("LocalPlayerState reference is missing in StaminaSystem."); return; }

        if (localPlayerState.isPlaying)
        {
            localPlayer.SetWalkSpeed(walkingSpeed);
            localPlayer.SetRunSpeed(runningSpeed);
        }
        else
        {
            localPlayer.SetWalkSpeed(observerWalkSpeed);
            localPlayer.SetRunSpeed(observerRunSpeed);
        }

    }

    // Called by LocalPlayerState when stamina is switched on or off.
    public void ResetStamina()
    {
        // Start() may not have run yet (first frames of the world).
        if (!Utilities.IsValid(localPlayer)) return;

        // Gives the normal run speed back if exhaustion had limited it.
        currentStamina = maxStamina;
        isExhausted = false;
        movementSpeedRestricted = false;
        ToggleMaxSpeed();
        UpdateDebugDisplay();
    }

    public override void PostLateUpdate()
    {
        if (hudRoot == null || !Utilities.IsValid(localPlayer)) return;

        VRCPlayerApi.TrackingData head = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
        Vector3 targetPos = head.position + head.rotation * hudOffset;
        Quaternion targetRot = head.rotation;

        if (hudFollowSpeed <= 0f)
        {
            hudRoot.SetPositionAndRotation(targetPos, targetRot);
            return;
        }

        float k = 1f - Mathf.Exp(-hudFollowSpeed * Time.deltaTime);
        hudRoot.SetPositionAndRotation(
            Vector3.Lerp(hudRoot.position, targetPos, k),
            Quaternion.Slerp(hudRoot.rotation, targetRot, k));
    }
}