using TMPro;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

/// <summary>
/// Two-handed gun: rifle or shotgun (same script, different inspector values / prefab variant).
///
/// AMMO (local only, NOT synced)
///  Only the owner's client fires, so only it needs the round count. Remote clients just replay effects from shotCount.
///  Ammo resets to full whenever THIS client becomes the owner (someone else picked the gun up). Re-grabbing your own
///  dropped gun keeps your rounds.
///
/// RELOAD
///  Reload = roundsInGun = min(roundsInGun + roundsPerReload, ammoCapacity), applied after reloadSeconds.
///  No firing while reloading. Dropping the gun cancels the reload.
///  Rifle:   capacity 31, roundsPerReload 30, 3 s.   (0 rounds -> 30, 1 or more -> 31)
///  Shotgun: capacity 9,  roundsPerReload 1,  0.5 s. (one shell per tap)
///  Input: VR = trigger click on the magazine collider (GunReloadZone), desktop = desktopReloadKey while holding.
///
/// SHOTGUN
///  isShotgun ON: each shot spawns pelletCount pellets in a FIXED pattern (1 in the centre, the rest in a ring
///  spreadAngle degrees off the barrel axis). Pellets use BallisticManager.SpawnPellet (speed falls off with distance).
///  isShotgun OFF: one normal bullet.
///
/// SIGHTS
///  'sights' holds the sight objects inside Gun inner: index 0 = iron, 1 = holographic, 2 = scope (an empty slot = this gun
///  has no such sight). Exactly one is active. The chosen index is SYNCED (syncSight), so everyone sees it and late joiners
///  get it. GunSightSelect (on the stationary cubes) calls SetSight() on the local player's held gun.
///  The scope camera + lens run ONLY while the LOCAL player holds this gun with the scope selected (checked every frame in
///  PostLateUpdate, so a drop, a steal or a player leaving can never leave a camera running).
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class TwoHandGun : UdonSharpBehaviour
{
    [Header("Gun References")]
    public VRC_Pickup pistol;            // the pickup on this same object
    public VRC_Pickup foregrip;          // ForegripHandle's pickup
    public Transform gunInner;
    public Transform foregripAnchor;
    public Transform muzzle;             // empty at the barrel tip, +Z pointing down the barrel (inside GunInner)
    public ParticleSystem particleBullet;      // cosmetic only
    public ParticleSystem particleSmoke;
    public int smokeCount = 5;
    public AudioSource audioSource;
    public AudioClip gunshotClip;
    public BallisticManager ballistics;  // optional: found by name "BallisticManager" if left empty

    [Header("Fire Settings")]
    [Tooltip("Owner-only logic, safe to toggle locally from a UI. True for Rifles, false for Shotguns.")]
    public bool fullAuto = true;
    [Tooltip("Fire rate in rounds per minute. 700/850 for AK/HK rifles, 90 for shotguns. Only affects the owner, remote clients just replay the synced shotCount.")]
    public int fireRate = 700;
    [Tooltip("Muzzle velocity in metres per second. 890/945 for AK/HK rifles, 400 for shotguns. Only affects the owner, remote clients just replay the synced shotCount.")]
    public float muzzleVelocity = 890f;  // m/s

    [Header("Shotgun")]
    public bool isShotgun = false;
    [Tooltip("Pellets per shot: 1 in the centre, the rest evenly spaced in a ring.")]
    public int pelletCount = 9;
    [Tooltip("Angle in degrees between the barrel axis and the ring pellets. 2 degrees = about 0.35 m at 10 m, 1 m at 30 m.")]
    public float spreadAngle = 2f;

    [Header("Ammo and Reload")]
    [Tooltip("Most rounds the gun can hold, magazine + chamber. Rifle 31, shotgun 9.")]
    public int ammoCapacity = 31;
    [Tooltip("Rounds added by one reload (never above the capacity). Rifle 30, shotgun 1.")]
    public int roundsPerReload = 30;
    [Tooltip("Seconds one reload takes. The gun cannot fire during it. Rifle 3, shotgun 0.5.")]
    public float reloadSeconds = 3f;
    [Tooltip("Desktop players press this key to reload while holding the gun.")]
    public KeyCode desktopReloadKey = KeyCode.F;
    [Tooltip("Optional: the GunReloadZone on the magazine collider (VR trigger click).")]
    public GunReloadZone reloadZone;
    [Tooltip("Optional: 3D text for rounds + reload status. Only shown to the player holding the gun.")]
    public TMP_Text ammoTextRight;
    [Tooltip("Optional: 3D text for rounds + instructions. Only shown to the player holding the gun.")]
    public TMP_Text ammoTextLeft;
    [Tooltip("Optional: click played when reload or when the trigger is pulled on an empty gun.")]
    public AudioClip gunMechanicalClickClip;

    [Header("Two-hand Settings")]
    public bool foregripVROnly = true;
    public float minHandDistance = 0.05f;   // metres; closer than this keeps the last valid aim
    public float remoteSmoothing = 15f;     // higher = snappier remote aim

    [Header("Sights")]
    [Tooltip("Sight objects inside Gun inner. Index 0 = iron, 1 = holographic, 2 = scope. Leave a slot empty if this gun has no such sight.")]
    public GameObject[] sights;
    [Tooltip("The scope's camera. Switched on only for the local player holding this gun with the scope selected. Leave empty if there is no scope.")]
    public Camera scopeCamera;
    [Tooltip("The scope's lens renderer (shows the camera image). Switched on and off together with the camera. Optional.")]
    public Renderer scopeLens;

    [Header("Remote Shot Playback")]
    public int maxRemoteBacklog = 6;        // max queued cosmetic shots on remote clients

    // ---- synced ----
    [UdonSynced] private bool syncTwoHand;
    [UdonSynced] private Vector3 syncAimLocal;   // aim direction in GunRoot local space
    [UdonSynced] private int shotCount;          // increments once per shot by the owner
    [UdonSynced] private int syncSight = 1;          // 0 = iron, 1 = holographic, 2 = scope (default 0 = iron)

    // ---- sights (local bookkeeping) ----
    private const int SIGHT_SCOPE = 2;
    private int shownSight = -1;                 // the sight currently switched on (-1 = not applied yet)
    private bool scopeViewOn;                    // scope camera + lens currently on

    // ---- aim ----
    private Vector3 restDirLocal;
    private Quaternion restLook;
    private Quaternion localTarget = Quaternion.identity;

    // ---- firing (owner) ----
    private float nextFireTime;
    private bool triggerHeld;
    private bool triggerPressed;

    // ---- ammo / reload (local, not synced) ----
    private int roundsInGun;
    private bool reloading;
    private float reloadEndTime;
    private Vector3[] pelletDirs;   // pellet directions in muzzle space (fixed pattern)

    // ---- firing (remote playback) ----
    private int lastSeenShots;
    private int remotePending;
    private float remoteNextShot;
    private bool syncReceived;

    void Start()
    {
        // assumes gunInner is at identity local rotation in the prefab
        restDirLocal = transform.InverseTransformPoint(foregripAnchor.position);
        restLook = Quaternion.LookRotation(restDirLocal, Vector3.up);
        syncAimLocal = restDirLocal;
        foregrip.pickupable = false;

        if (ballistics == null)
        {
            GameObject go = GameObject.Find("BallisticManager");
            if (go != null) ballistics = go.GetComponent<BallisticManager>();
        }

        roundsInGun = ammoCapacity;
        reloading = false;
        BuildPelletPattern();
        if (reloadZone != null) reloadZone.SetUsable(false);
        ShowAmmoText(false);

        // (syncSight is NOT assigned here: a late joiner may already have received the real value)
        ApplySight();
        UpdateScopeView(false);
    }

    // Fixed pattern in muzzle space: index 0 = straight ahead, the others on a ring around it.
    private void BuildPelletPattern()
    {
        int n = isShotgun ? Mathf.Max(pelletCount, 1) : 1;
        pelletDirs = new Vector3[n];
        pelletDirs[0] = Vector3.forward;
        if (n <= 1) return;

        float t = Mathf.Tan(spreadAngle * Mathf.Deg2Rad);
        float step = 6.2831853f / (float)(n - 1);
        for (int i = 1; i < n; i++)
        {
            float a = (float)(i - 1) * step;
            Vector3 d = new Vector3(Mathf.Sin(a) * t, Mathf.Cos(a) * t, 1f);
            pelletDirs[i] = d.normalized;
        }
    }

    // ---------------- pickup events (local player is the holder) ----------------

    public override void OnPickup()
    {
        foregrip.pickupable = !foregripVROnly || Networking.LocalPlayer.IsUserInVR();
        if (reloadZone != null) reloadZone.SetUsable(true);
        ShowAmmoText(true);
    }

    public override void OnDrop()
    {
        triggerHeld = false;
        triggerPressed = false;
        if (foregrip.IsHeld) foregrip.Drop();
        foregrip.pickupable = false;
        syncTwoHand = false;
        syncAimLocal = restDirLocal;
        localTarget = Quaternion.identity;

        CancelReload();
        if (reloadZone != null) reloadZone.SetUsable(false);
        ShowAmmoText(false);
    }

    public override void OnPickupUseDown()
    {
        if (!Networking.IsOwner(gameObject) || !pistol.IsHeld) return;
        triggerHeld = true;
        triggerPressed = true;
    }

    public override void OnPickupUseUp()
    {
        triggerHeld = false;
    }

    // Someone else picked the gun up (ownership came to THIS client): start with a full gun.
    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        if (Utilities.IsValid(player) && player.isLocal) ResetAmmo();
    }

    // ---------------- sync ----------------

    public override void OnDeserialization()
    {
        // first data a late joiner receives: don't replay the gun's whole shot history
        if (!syncReceived)
        {
            syncReceived = true;
            lastSeenShots = shotCount;
        }

        // the owner changed the sight (or this is a late joiner's first data): show it
        ApplySight();
    }

    // ---------------- main loop ----------------

    public override void PostLateUpdate()
    {
        bool isOwner = Networking.IsOwner(gameObject);
        bool local = isOwner && pistol.IsHeld;

        // ----- scope camera: only while the local player holds this gun with the scope selected -----
        bool wantScope = local && syncSight == SIGHT_SCOPE;
        if (wantScope != scopeViewOn) UpdateScopeView(wantScope);

        // ----- aim -----
        if (local)
        {
            bool two = foregrip.IsHeld;
            if (two)
            {
                Vector3 dWorld = foregrip.transform.position - transform.position;
                if (dWorld.sqrMagnitude >= minHandDistance * minHandDistance)
                {
                    Vector3 d = transform.InverseTransformDirection(dWorld);
                    if (Mathf.Abs(Vector3.Dot(d.normalized, Vector3.up)) < 0.999f)
                    {
                        localTarget = Quaternion.LookRotation(d, Vector3.up) * Quaternion.Inverse(restLook);
                        syncAimLocal = d;
                    }
                }
            }
            else
            {
                localTarget = Quaternion.identity;
                syncAimLocal = restDirLocal;
            }

            syncTwoHand = two;
            gunInner.localRotation = localTarget;
        }
        else
        {
            Quaternion target = Quaternion.identity;
            if (syncTwoHand && syncAimLocal.sqrMagnitude > 0.0001f)
            {
                target = Quaternion.LookRotation(syncAimLocal, Vector3.up) * Quaternion.Inverse(restLook);
            }
            float k = 1f - Mathf.Exp(-remoteSmoothing * Time.deltaTime);
            gunInner.localRotation = Quaternion.Slerp(gunInner.localRotation, target, k);
        }

        // ----- park the foregrip handle when nobody is using it locally -----
        if (!(local && foregrip.IsHeld))
        {
            foregrip.transform.SetPositionAndRotation(
                foregripAnchor.position, foregripAnchor.rotation);
        }

        // ----- reload (before firing, so a finished reload can fire this frame) -----
        if (local)
        {
            if (Input.GetKeyDown(desktopReloadKey)) TryReload();
            UpdateReload();
        }
        else if (reloading)
        {
            CancelReload();   // safety: lost the gun without OnDrop
        }

        // ----- firing (after aim, so the muzzle pose is this frame's) -----
        if (isOwner)
        {
            lastSeenShots = shotCount;   // never replay our own shots
            remotePending = 0;

            if (local && (triggerPressed || (fullAuto && triggerHeld)))
            {
                TryFire();
            }
            triggerPressed = false;
        }
        else
        {
            ProcessRemoteShots();
        }
    }

    // ---------------- sights ----------------

    // Called by GunSightSelect on the LOCAL client. Only the holder may change the sight.
    // Returns true when the gun now has that sight (changed or already on), false when it was refused.
    public bool SetSight(int index)
    {
        if (!Networking.IsOwner(gameObject) || !pistol.IsHeld) return false;
        if (!HasSight(index)) return false;
        if (index == syncSight) return true;

        syncSight = index;   // Continuous sync sends it by itself
        ApplySight();        // the owner does not get OnDeserialization
        return true;
    }

    private bool HasSight(int index)
    {
        return sights != null && index >= 0 && index < sights.Length && sights[index] != null;
    }

    // Switches exactly one sight object on. Does nothing when it is already showing syncSight.
    private void ApplySight()
    {
        if (sights == null || syncSight == shownSight) return;
        shownSight = syncSight;

        for (int i = 0; i < sights.Length; i++)
        {
            if (sights[i] != null) sights[i].SetActive(i == syncSight);
        }
    }

    // Camera and lens are set through their own references, NOT through the Scope script: that script sits on an object
    // that is inactive while the scope is not selected, and its Start would only run later and could undo this.
    private void UpdateScopeView(bool on)
    {
        scopeViewOn = on;
        if (scopeCamera != null) scopeCamera.enabled = on;
        if (scopeLens != null) scopeLens.enabled = on;
    }

    // ---------------- reload ----------------

    // Called by the GunReloadZone (VR trigger click) and by the desktop key. Only does something for the holder.
    public void TryReload()
    {
        if (reloading) return;
        if (!Networking.IsOwner(gameObject) || !pistol.IsHeld) return;
        if (roundsInGun >= ammoCapacity) return;

        reloading = true;
        reloadEndTime = Time.time + reloadSeconds;

        // a trigger still held from before the reload must not fire the moment it ends: release and squeeze again
        triggerHeld = false;
        triggerPressed = false;

        UpdateAmmoText();
        if (!isShotgun) PlayGunClick();
    }

    private void UpdateReload()
    {
        if (!reloading) return;
        if (Time.time < reloadEndTime) return;

        reloading = false;
        roundsInGun = Mathf.Min(roundsInGun + roundsPerReload, ammoCapacity);
        UpdateAmmoText();
        PlayGunClick();
    }

    private void CancelReload()
    {
        if (!reloading) return;
        reloading = false;
        UpdateAmmoText();
        PlayGunClick();
    }

    private void ResetAmmo()
    {
        roundsInGun = ammoCapacity;
        reloading = false;
        UpdateAmmoText();
        PlayGunClick();
    }

    // ---------------- ammo text ----------------

    private void ShowAmmoText(bool visible)
    {
        if (ammoTextRight == null) return;
        if (ammoTextLeft == null) return;
        ammoTextRight.gameObject.SetActive(visible);
        ammoTextLeft.gameObject.SetActive(visible);
        if (visible) UpdateAmmoText();
    }

    private void UpdateAmmoText()
    {
        if (ammoTextRight == null) return;
        if (ammoTextLeft == null) return;

        string tright = roundsInGun.ToString() + " / " + ammoCapacity.ToString();
        string tleft = tright + "\nVR Reload: Interact\nDesktop Reload: F";
        if (reloading) tright += "\nRELOADING";
        else if (roundsInGun <= 0) tright += "\nEMPTY";
        else tright += "\nREADY";
        ammoTextRight.text = tright;
        ammoTextLeft.text = tleft;
    }

    // ---------------- firing ----------------

    private float FireInterval()
    {
        return 60f / Mathf.Max(1, fireRate);
    }

    private void TryFire()
    {
        if (reloading) return;

        if (roundsInGun <= 0)
        {
            if (triggerPressed) PlayGunClick();   // click once per pull, not every frame in full auto
            return;
        }

        if (Time.time < nextFireTime) return;

        // keep a steady cadence across frames; restart the schedule if we fell behind
        float interval = FireInterval();
        nextFireTime = (Time.time - nextFireTime < interval)
            ? nextFireTime + interval
            : Time.time + interval;

        roundsInGun--;

        // 1) tell remote clients (synced counter, no network event per bullet)
        shotCount++;

        // 2) instant cosmetics for the shooter
        PlayShotEffects();

        // 3) the real bullet(s): simulated and hit-tested only on this client
        if (ballistics != null && muzzle != null)
        {
            if (isShotgun) FirePellets();
            else ballistics.Spawn(muzzle.position, muzzle.forward * muzzleVelocity);
        }

        UpdateAmmoText();
    }

    private void FirePellets()
    {
        Vector3 origin = muzzle.position;
        for (int i = 0; i < pelletDirs.Length; i++)
        {
            Vector3 dir = muzzle.TransformDirection(pelletDirs[i]);
            ballistics.SpawnPellet(origin, dir * muzzleVelocity);
        }
    }

    private void ProcessRemoteShots()
    {
        int delta = shotCount - lastSeenShots;
        lastSeenShots = shotCount;
        if (delta > 0)
        {
            remotePending = Mathf.Min(remotePending + delta, maxRemoteBacklog);
        }

        if (remotePending > 0 && Time.time >= remoteNextShot)
        {
            remotePending--;
            remoteNextShot = Time.time + FireInterval();
            PlayShotEffects();
        }
    }

    private void PlayShotEffects()
    {
        if (particleBullet != null)
        {
            if (!isShotgun)
            {
                particleBullet.Emit(1);
            }
            else
            {
                particleBullet.Emit(pelletCount);
            }
        }
        if (particleSmoke != null) particleSmoke.Emit(smokeCount);

        if (audioSource != null && gunshotClip != null)
        {
            audioSource.pitch = Random.Range(0.9f, 1.1f);
            if (isShotgun) audioSource.pitch = Random.Range(0.4f, .5f);
            audioSource.volume = 1f;
            audioSource.PlayOneShot(gunshotClip);
        }
    }

    private void PlayGunClick()
    {
        if (audioSource != null && gunMechanicalClickClip != null)
        {
            audioSource.pitch = Random.Range(0.9f, 1.1f);
            audioSource.volume = 0.5f;
            audioSource.PlayOneShot(gunMechanicalClickClip);
        }
    }
}
