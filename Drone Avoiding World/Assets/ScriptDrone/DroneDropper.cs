using UdonSharp;
using UnityEngine;
using VRC.Udon;
using VRC.SDKBase;
using VRC.SDK3.Components;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// High-flying bomber drone, networked. Same network model as DroneChaser (the owner = Game Host's client runs the AI,
/// everyone else only displays), but it flies in a straight line: NO NavMeshAgent. At 30-75 m a Dropper should not
/// path around buildings, and it must be able to hover over a player standing on something that is not on the navmesh.
///
/// STATES
/// PARKED   : inert at the parking spot until commanded.
/// TRAVEL   : fly to a commanded destination at cruiseHeight. Spots players on the way (camera looks straight down).
/// PATROL   : circle a center point clockwise, radius grows over time, at cruiseHeight.
/// ATTACK   : (has bombs) flies at cruiseHeight to the spotted player, descends to attackHeight (above the player's feet)
///            once it is within descendStartDistance, hovers over the player matching the player's velocity and drops a bomb
///            when: player seen, at attack height, over the player, player not running, nothing in between, bombInterval over.
///            Lost for loseSightTime -> PATROL around the last seen position.
/// TRAIL    : (out of bombs) climbs to trailHeight and circles the player at trailRadius. Still spots and reports.
/// EXPLODED : effect only (harmless), hidden, waits for the Captain to call ResetToHome().
///
/// BOMBS
/// A bomb starts at the Dropper's belly with the Dropper's horizontal velocity (optional) and ZERO vertical speed,
/// then falls at bombGravity (1 g). It explodes on contact with the world (obstacleMask) or with a player's body.
/// Bombs have NO collider, so a bullet can never hit them. Every client simulates every bomb by itself from the
/// synced start values (DropperNetSync), and each client decides for ITS OWN player: body contact and blast radius,
/// same logic as the Chaser's explosion. A shot-down Dropper does not drop its bombs, bombs already falling keep falling.
///
/// NETWORK MODEL
/// - Owner: all AI, movement (root x/z), altitude, target choice, bomb release.
/// - Other clients: root position from VRC Object Sync, state / altitude / yaw / bombs left / bomb drops / explosions
///   from the synced variables on the DropperNetSync child object.
/// - Shots: whoever's client registers the hit calls OnShot(); it is forwarded to the owner.
///
/// Horizontal movement = root transform (x/z only, root Y stays at the ground height of the last launch).
/// Vertical = altitude controller on the Flying Group (world Y).
/// </summary>
// NoVariableSync (network events + ownership, no variables): VRC Object Sync cannot share an object with a Manual behaviour.
// All synced variables live on the DropperNetSync child object (field 'net').
[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class DroneDropper : UdonSharpBehaviour
{
    [Header("References")]
    public Transform flyingGroup;       // child holding mesh + collider; its world Y is driven here
    public Transform eye;               // child "camera" point, pitched STRAIGHT DOWN (X rotation 90). Keep it OUTSIDE the tilt pivot
    public LayerMask obstacleMask;      // walls, roofs, trees, terrain, buildings. Exclude player layers and the OpFor layer
    public GameObject explosionEffect;  // the Dropper's own death effect (visual only). Must NOT be a child of flyingGroup

    [Header("Multiplayer")]
    public GameManager gameManager;     // the drone's owner follows the Game Host. Empty = no ownership logic (solo test)
    public PlayerRoster roster;         // who may be targeted (GetTargets). Empty = local player only
    public LocalPlayerState localState; // how THIS client kills its own player
    public VRCObjectSync objectSync;    // optional, on this root: avoids a visible slide after a reset
    public DropperNetSync net;          // REQUIRED: child object holding the synced variables (Manual sync)
    public float syncInterval = 0.1f;   // max rate of altitude / yaw updates
    public float remoteAltSmoothing = 10f; // other clients: how fast the altitude follows the synced value
    public OpForCaptain captain;        // the manager. When set it launches, re-sends and respawns this drone

    [Header("Tilt animation (cosmetic, runs on every client)")]
    public Animator tiltAnimator;          // Animator on TiltPivot. Empty = no tilt
    public string forwardParam = "VelForward";
    public string sideParam = "VelSide";
    public float tiltFullSpeed = 8f;       // speed (m/s) that counts as full tilt
    public float tiltSmoothing = 8f;       // higher = snappier
    public float tiltFullTurnRate = 120f;  // yaw rate (deg/s) that counts as a full bank into a turn
    public float tiltTurnMinSpeed = 3f;    // speed (m/s) for full turn banking; slower = less bank (hovering and spinning in place does not lean)

    [Header("Altitude (world Y)")]
    public float referenceGroundY = 0f; // world Y of your flat ground; Travel / Patrol heights are measured from this
    public float cruiseHeight = 75f;    // Travel, Patrol and the approach in Attack
    public float attackHeight = 30f;    // Attack: above the target's FEET (so a player on a hill or roof still gets a 30 m drop)
    public float trailHeight = 50f;     // Trail: above the target's feet
    public float minClearance = 1f;     // parked: height above the ground
    public float hardFloorClearance = 0.25f; // never below root height + this
    public float maxClimbSpeed = 8f;
    public float climbAccel = 4f;
    public float maxDescendSpeed = 10f;
    public float descendAccel = 5f;

    [Header("Movement")]
    public float travelSpeed = 15f;
    public float patrolSpeed = 3f;
    public float attackSpeed = 8f;
    public float trailSpeed = 8f;          // lighter now: must beat a running player (4 m/s) plus the circle (trailOrbitSpeed)
    public float acceleration = 8f;        // horizontal, every state
    public float trackGain = 1.5f;         // 1/s: how hard it pulls toward a moving goal (higher = tighter, may overshoot)
    public float turnSpeed = 180f;         // degrees per second (cosmetic yaw)
    public float arriveDistance = 3f;      // Travel: close enough to the destination

    [Header("Patrol (clockwise ring, growing)")]
    public float patrolStartRadius = 5f;
    public float patrolRadiusGrowth = 0.5f;   // meters per second
    public float patrolMaxRadius = 50f;
    public float patrolStepDistance = 10f;    // roughly how far apart ring waypoints are
    public float patrolArriveDistance = 3f;

    [Header("Awareness")]
    public int spotMinPoints = 1;          // visible points (of feet/chest/head) needed to SPOT a player
    public float loseSightTime = 4f;       // unseen this long -> Patrol around the last seen position
    public float spotReactionTime = 1f;    // no bomb for this long after first spotting
    public float reportInterval = 2f;      // Attack / Trail: how often it reports the player's location

    [Header("Vision (camera looks straight down: ground radius = height x tan(viewAngle / 2))")]
    public float viewAngle = 90f;          // total cone angle
    public float viewRange = 120f;         // at 75 m up the cone edge is 106 m away
    public float visionInterval = 0.25f;
    public int minVisiblePoints = 2;       // to DROP: at least this many of feet / chest / head

    [Header("Attack")]
    public float descendStartDistance = 20f;  // horizontal distance to the player at which it starts to descend
    public float descendCancelDistance = 45f; // the player got this far away: climb back to cruiseHeight
    public float dropAlignRadius = 2f;        // horizontal distance to the player at which a bomb may be released
    public float heightTolerance = 1.5f;      // 'at attack height' = within this
    public float dropMaxTargetSpeed = 3f;     // above this the player counts as running: wait. Stamina: walk 2, run 4
    public float targetVelSmoothing = 4f;     // 1/s, smoothing of the measured player velocity

    [Header("Trail (out of bombs)")]
    public float trailRadius = 5f;
    public float trailOrbitSpeed = 2f;        // m/s along the circle (clockwise seen from above)

    [Header("Bombs")]
    public int bombCount = 3;
    public float bombInterval = 7f;           // min seconds between two releases (a fall from 30 m takes about 2.5 s)
    public float bombGravity = 9.81f;         // 1 g
    public bool bombInheritsVelocity = true;  // release with the Dropper's horizontal velocity (realistic: the bomb flies on with it)
    public float bombDropOffset = 0.5f;       // release point this far below the flying group
    public float bombMaxLifetime = 10f;        // a bomb that never touches anything vanishes after this
    public float bombHitRadius = 0.5f;        // contact distance to a player's body (bomb size + player size)
    public float bombKillRadius = 5f;         // a player inside this distance of an explosion is killed
    public float bombCoreRadius = 0.5f;       // inside this the blast kills through walls; out to bombKillRadius it needs line of sight
    public float blastHalfWidth = 0.35f;      // sideways ray spread; wider than a body so thin poles can't hide a player
    public bool bombAlignToVelocity = true;   // bomb model's +Z (nose) points along its flight path
    public GameObject[] bombObjects;          // pool of falling bomb visuals: NO colliders, inactive, children of the root (NOT of FlyingGroup). Assign at least bombCount
    public GameObject[] bombEffects;          // one explosion effect per bomb object (same index), inactive, NOT children of the bomb objects
    public GameObject[] carriedBombs;         // optional: bomb meshes under the belly (children of FlyingGroup), shown while bombsLeft > index

    [Header("Debug (set these in the editor BEFORE building; builds have no inspector)")]
    public bool debugIgnoreVision;         // drone always sees every eligible player (cone and obstacles ignored)
    public bool debugLog;                  // log state changes and events
    public bool debugDrawRays;             // draw vision rays in the Scene view
    public bool debugDropReasons;          // log WHY a bomb was not released (throttled)
    public bool debugKillAnyTarget;        // target and kill ANY player, not only players in roster slots
    public bool debugAutoDispatch = false; // Parked drone dispatches itself. Turn OFF when the manager sends commands.
    public float debugDispatchDelay = 1f;
    public Transform debugTravelTarget;    // optional: auto-dispatch travels here, otherwise it patrols where it stands
    public bool debugAutoRespawn = false;  // Exploded drone resets itself. Turn OFF when the manager does it.
    public float debugRespawnDelay = 3f;
    public bool debugTestShot;             // tick in play mode to simulate a gun hit

    private const int STATE_PARKED = 0;
    private const int STATE_TRAVEL = 1;
    private const int STATE_PATROL = 2;
    private const int STATE_ATTACK = 3;
    private const int STATE_TRAIL = 4;
    private const int STATE_EXPLODED = 5;

    // (the synced variables live on the DropperNetSync object, see 'net')

    // ---- network bookkeeping ----
    private bool aiActive;              // true on the owner only
    private bool initialized;
    private bool gotFirstSync;
    private int lastExplosionSeq;
    private int lastBombSeq;
    private float followHostTimer;
    private float syncTimer;
    private float lastSentAlt;
    private float lastSentYaw;
    private float shotLockUntil;     // shooter's client: one hit per blast (a shotgun lands up to 9 pellets at once)

    // ---- state (on non-owners 'state' and 'bombsLeft' mirror the synced values) ----
    private int state;
    private int bombsLeft;
    private float currentAlt;
    private float altVel;
    private Vector3 horizVel;
    private float cooldown;             // time until the next bomb may be released
    private float visionTimer;
    private float dispatchTimer;
    private float explodeTimer;
    private float reportTimer;
    private Vector3 launchPos;

    // tilt animation
    private bool tiltInit;
    private Vector3 lastFlyPos;
    private float tiltFwd;
    private float tiltSide;
    private float lastFlyYaw;

    // ---- targeting (owner only) ----
    private VRCPlayerApi[] candidates = new VRCPlayerApi[82];
    private VRCPlayerApi targetPlayer;
    private int targetId;
    private bool hasTarget;
    private Vector3 targetFeet;
    private float targetBodyHeight;
    private Vector3 targetVel;          // horizontal velocity of the target, smoothed
    private int velTrackId = -1;        // whose velocity targetVel is

    // vision results for the current target (updated at visionInterval)
    private int visibleCount;
    private Vector3 lastSeenPos;
    private float unseenTimer;

    // travel / patrol
    private Vector3 travelDest;
    private Vector3 patrolCenter;
    private Vector3 patrolPoint;
    private float patrolAngle;     // radians, increasing = clockwise seen from above
    private float patrolTime;

    // attack / trail
    private bool attackLow;
    private float trailAngle;      // radians, increasing = clockwise seen from above
    private float nextReasonLog;

    // ---- bombs (simulated on EVERY client) ----
    private int poolSize;
    private bool[] bombActive;
    private Vector3[] bombPos;
    private Vector3[] bombVel;
    private float[] bombAge;
    private float[] bombCatchUp;        // seconds this bomb is already "in the past" when a remote client learns about it
    private Vector3 contactPoint;

    void Start()
    {
        launchPos = transform.position;
        currentAlt = flyingGroup.position.y;
        cooldown = 1f;
        bombsLeft = bombCount;
        ShowCarried(bombsLeft);

        poolSize = (bombObjects != null && bombObjects.Length > 0) ? bombObjects.Length : Mathf.Max(bombCount, 2);
        bombActive = new bool[poolSize];
        bombPos = new Vector3[poolSize];
        bombVel = new Vector3[poolSize];
        bombAge = new float[poolSize];
        bombCatchUp = new float[poolSize];

        for (int i = 0; i < poolSize; i++)
        {
            GameObject v = BombVisual(i);
            if (v != null) v.SetActive(false);
            GameObject f = BombEffect(i);
            if (f != null) f.SetActive(false);
        }
        if (explosionEffect != null) explosionEffect.SetActive(false);
        if (bombObjects == null || bombObjects.Length < bombCount)
        {
            Debug.LogWarning("[Dropper] " + gameObject.name + ": fewer Bomb Objects than bombCount, some bombs will be invisible");
        }

        ApplyOwnership();
    }

    // ================= ownership =================

    // Owner = runs the AI. Everyone else only displays.
    void ApplyOwnership()
    {
        bool owner = Networking.IsOwner(gameObject);
        if (initialized && owner == aiActive) return;
        bool first = !initialized;
        initialized = true;

        if (!owner)
        {
            aiActive = false;
            return;
        }

        aiActive = true;
        ClaimNet();
        horizVel = Vector3.zero;
        altVel = 0f;

        // Continue from whatever the previous owner last showed ('state' and 'bombsLeft' mirror it on non-owners).
        int adopt = first ? STATE_PARKED : state;
        if (adopt == STATE_EXPLODED)
        {
            state = STATE_EXPLODED;
            explodeTimer = debugRespawnDelay;
            SyncNow();
        }
        else if (adopt == STATE_TRAVEL)
        {
            SetState(STATE_PARKED);
            CommandTravel(travelDest);
        }
        else if (adopt == STATE_PARKED)
        {
            SetState(STATE_PARKED);
        }
        else
        {
            StartPatrol(transform.position);
        }
    }

    // The sync object must be owned by the same client as the drone, or its RequestSerialization is ignored.
    void ClaimNet()
    {
        if (net == null) return;
        if (!Networking.IsOwner(net.gameObject)) Networking.SetOwner(Networking.LocalPlayer, net.gameObject);
    }

    public override void OnOwnershipTransferred(VRCPlayerApi player)
    {
        ApplyOwnership();
    }

    // The Game Host's client takes ownership of the drone (checked once a second on every client).
    void FollowGameHost()
    {
        if (gameManager == null) return;
        if (!gameManager.IsGameHost()) return;
        if (Networking.IsOwner(gameObject)) return;
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
    }

    // ================= public API (manager / relay scripts) =================

    // Manager -> drone (owner only). Ignored while attacking, trailing or exploded.
    public void CommandTravel(Vector3 destination)
    {
        if (!aiActive) return;
        if (state == STATE_ATTACK || state == STATE_TRAIL || state == STATE_EXPLODED) return;
        travelDest = destination;
        SetState(STATE_TRAVEL);
    }

    // Manager -> drone (owner only), after an explosion (or any time). Puts the drone back at the launch point, bombs reloaded.
    public void ResetToLaunch(Vector3 pos)
    {
        if (!aiActive) return;
        transform.position = pos;
        if (objectSync != null) objectSync.FlagDiscontinuity();
        horizVel = Vector3.zero;
        flyingGroup.gameObject.SetActive(true);
        currentAlt = pos.y + minClearance;
        altVel = 0f;
        flyingGroup.position = new Vector3(pos.x, currentAlt, pos.z);
        ResetTilt();
        cooldown = bombInterval;
        unseenTimer = 0f;
        visionTimer = 0f;
        targetPlayer = null;
        hasTarget = false;
        velTrackId = -1;
        targetVel = Vector3.zero;
        attackLow = false;
        bombsLeft = bombCount;
        ShowCarried(bombsLeft);
        SetState(STATE_PARKED);
    }

    // ---- OpForCaptain API (owner only: on other clients the queries return false and the commands do nothing) ----

    public bool IsParked()
    {
        return aiActive && state == STATE_PARKED;
    }

    public bool IsExploded()
    {
        return aiActive && state == STATE_EXPLODED;
    }

    // Free = may take a new order: Travel or Patrol. Attack and Trail (it has bombed or is bombing) and Exploded ignore orders.
    public bool IsFreeForOrders()
    {
        return aiActive && (state == STATE_TRAVEL || state == STATE_PATROL);
    }

    // Where this drone is heading (Travel) or circling (Patrol).
    public Vector3 GetCommandAnchor()
    {
        if (state == STATE_TRAVEL) return travelDest;
        return patrolCenter;
    }

    // Wave launch: appear at spawnPos (a LaunchArea) and fly to destination.
    public void LaunchFrom(Vector3 spawnPos, Vector3 destination)
    {
        if (!aiActive) return;
        ResetToLaunch(spawnPos);
        CommandTravel(destination);
    }

    // Back to the spot where this drone was placed in the scene (the shared pool's parking spot).
    public void ResetToHome()
    {
        ResetToLaunch(launchPos);
    }

    // BallisticManager calls this (via SendCustomEvent) on the shooter's client when a bullet hits the Dropper's collider.
    public void OnShot()
    {
        if (Time.time < shotLockUntil) return;   // the pellets of one blast arrive within a few frames: count them once
        shotLockUntil = Time.time + 0.3f;
        if (aiActive) Explode();
        else SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(NetShot));
    }

    public void OnBulletHit()
    {
        OnShot();
    }

    // Network event: runs on the owner.
    public void NetShot()
    {
        Explode();
    }

    // ================= update =================

    void Update()
    {
        float dt = Time.deltaTime;

        followHostTimer -= dt;
        if (followHostTimer <= 0f)
        {
            followHostTimer = 1f;
            FollowGameHost();
        }

        if (debugTestShot)
        {
            debugTestShot = false;
            OnShot();
        }

        UpdateBombs(dt);       // every client simulates the falling bombs and checks its OWN player

        if (!aiActive) return; // other clients only display (see LateUpdate)

        if (state == STATE_EXPLODED) { UpdateExploded(dt); return; }
        if (state == STATE_PARKED) { UpdateParked(dt); return; }

        cooldown -= dt;

        RefreshTargetPosition(dt);

        visionTimer -= dt;
        if (visionTimer <= 0f)
        {
            visionTimer = visionInterval;
            ScanTargets();
        }

        if (state == STATE_TRAVEL) DoTravel(dt);
        else if (state == STATE_PATROL) DoPatrol(dt);
        else if (state == STATE_ATTACK) DoAttack(dt);
        else if (state == STATE_TRAIL) DoTrail(dt);
    }

    // ================= states =================

    void SetState(int s)
    {
        state = s;

        if (s == STATE_PARKED)
        {
            horizVel = Vector3.zero;
            dispatchTimer = debugDispatchDelay;
        }

        if (debugLog) Debug.Log("[Dropper] state -> " + StateName(s));
        SyncNow();
    }

    string StateName(int s)
    {
        if (s == STATE_PARKED) return "PARKED";
        if (s == STATE_TRAVEL) return "TRAVEL";
        if (s == STATE_PATROL) return "PATROL";
        if (s == STATE_ATTACK) return "ATTACK";
        if (s == STATE_TRAIL) return "TRAIL";
        return "EXPLODED";
    }

    // ---------- Parked ----------

    void UpdateParked(float dt)
    {
        if (captain != null || !debugAutoDispatch) return;
        dispatchTimer -= dt;
        if (dispatchTimer > 0f) return;

        if (debugTravelTarget != null) CommandTravel(debugTravelTarget.position);
        else StartPatrol(transform.position);
    }

    // ---------- Travel ----------

    void DoTravel(float dt)
    {
        if (CheckSpotted()) return;

        float d = HorizDistance(transform.position, travelDest);
        if (d < arriveDistance)
        {
            StartPatrol(travelDest);
            return;
        }

        SeekPoint(travelDest, travelSpeed, dt);
    }

    // ---------- Patrol ----------

    void StartPatrol(Vector3 center)
    {
        patrolCenter = center;
        patrolTime = 0f;

        // drop the old target so the next scan picks fresh
        targetPlayer = null;
        hasTarget = false;
        velTrackId = -1;
        targetVel = Vector3.zero;
        attackLow = false;

        Vector3 off = transform.position - center;
        off.y = 0f;
        if (off.sqrMagnitude > 0.01f) patrolAngle = Mathf.Atan2(off.x, off.z);
        else patrolAngle = Random.Range(0f, 6.2832f);

        SetState(STATE_PATROL);
        PickNextPatrolPoint();
    }

    void DoPatrol(float dt)
    {
        if (CheckSpotted()) return;

        patrolTime += dt;

        float d = HorizDistance(transform.position, patrolPoint);
        if (d < patrolArriveDistance) PickNextPatrolPoint();

        SeekPoint(patrolPoint, patrolSpeed, dt);
    }

    // Next waypoint further clockwise along the (growing) ring. Flying straight, so every point is reachable.
    void PickNextPatrolPoint()
    {
        float radius = Mathf.Min(patrolStartRadius + patrolRadiusGrowth * patrolTime, patrolMaxRadius);
        float step = Mathf.Clamp(patrolStepDistance / Mathf.Max(radius, 1f), 0.25f, 0.8f);

        patrolAngle += step;   // + = clockwise seen from above (0 = +Z, 90 degrees = +X)
        patrolPoint = patrolCenter + new Vector3(Mathf.Sin(patrolAngle), 0f, Mathf.Cos(patrolAngle)) * radius;
    }

    // ---------- Awareness helpers ----------

    bool CheckSpotted()
    {
        if (hasTarget && visibleCount >= spotMinPoints)
        {
            StartAttack();
            return true;
        }
        return false;
    }

    // Spotted a player: with bombs it attacks, without bombs it trails.
    void StartAttack()
    {
        lastSeenPos = targetFeet;
        unseenTimer = 0f;
        reportTimer = 0f;
        cooldown = Mathf.Max(cooldown, spotReactionTime);
        attackLow = false;

        if (bombsLeft > 0) SetState(STATE_ATTACK);
        else BeginTrail();
    }

    void BeginTrail()
    {
        Vector3 off = transform.position - targetFeet;
        off.y = 0f;
        if (off.sqrMagnitude > 0.01f) trailAngle = Mathf.Atan2(off.x, off.z);
        else trailAngle = Random.Range(0f, 6.2832f);
        SetState(STATE_TRAIL);
    }

    // Tells the Captain where a player was seen (it fills that player's location data).
    void ReportTick(float dt)
    {
        reportTimer -= dt;
        if (reportTimer > 0f) return;
        reportTimer = reportInterval;

        if (debugLog) Debug.Log("[Dropper] sighting reported at " + targetFeet);
        if (captain != null && Utilities.IsValid(targetPlayer)) captain.ReportSighting(targetPlayer, targetFeet);
    }

    // ---------- Attack ----------

    void DoAttack(float dt)
    {
        if (!hasTarget)
        {
            StartPatrol(lastSeenPos);
            return;
        }

        // ScanTargets resets unseenTimer whenever the target is seen
        unseenTimer += dt;
        if (unseenTimer >= loseSightTime)
        {
            StartPatrol(lastSeenPos);
            return;
        }

        // seen: be right above the player, moving with them. Not seen: go to where they were last seen.
        bool seenNow = visibleCount >= spotMinPoints;
        Vector3 goal = seenNow ? targetFeet : lastSeenPos;
        Vector3 goalVel = seenNow ? targetVel : Vector3.zero;
        TrackPoint(goal, goalVel, attackSpeed, dt);

        // descend when close (with hysteresis), so the approach stays at cruise height where the view is widest
        float horiz = HorizDistance(transform.position, goal);
        if (!attackLow && horiz <= descendStartDistance) attackLow = true;
        else if (attackLow && horiz > descendCancelDistance) attackLow = false;

        if (!seenNow) return;

        ReportTick(dt);
        TryDropBomb(horiz);
    }

    void TryDropBomb(float horiz)
    {
        if (bombsLeft <= 0) return;
        if (cooldown > 0f) return;

        if (visibleCount < minVisiblePoints) { LogDropBlock("vision: visiblePoints=" + visibleCount); return; }

        float height = currentAlt - targetFeet.y;
        if (height > attackHeight + heightTolerance) { LogDropBlock("still descending, height " + height); return; }

        if (horiz > dropAlignRadius) { LogDropBlock("not above the player yet, " + horiz + " m off"); return; }

        float speed = targetVel.magnitude;
        if (speed > dropMaxTargetSpeed) { LogDropBlock("player too fast, " + speed + " m/s"); return; }

        if (!BombPathClear()) { LogDropBlock("something is under the Dropper (roof, tree)"); return; }

        DropBomb();
    }

    // Straight down from the release point to the player's head must be free (a roof edge or a branch would eat the bomb).
    bool BombPathClear()
    {
        Vector3 from = flyingGroup.position + Vector3.down * bombDropOffset;
        float dist = from.y - (targetFeet.y + targetBodyHeight);
        if (dist < 0.1f) return true;
        RaycastHit hit;
        return !Physics.Raycast(from, Vector3.down, out hit, dist, obstacleMask, QueryTriggerInteraction.Ignore);
    }

    // Debug: why a bomb was not released (throttled to twice a second)
    void LogDropBlock(string why)
    {
        if (!debugDropReasons) return;
        if (Time.time < nextReasonLog) return;
        nextReasonLog = Time.time + 0.5f;
        Debug.Log("[Dropper] no bomb: " + why);
    }

    // Owner only: release one bomb (zero vertical speed, optionally the Dropper's horizontal velocity) and tell everyone.
    void DropBomb()
    {
        Vector3 pos = flyingGroup.position + Vector3.down * bombDropOffset;
        Vector3 vel = bombInheritsVelocity ? horizVel : Vector3.zero;
        vel.y = 0f;

        bombsLeft = bombsLeft - 1;
        cooldown = bombInterval;
        ShowCarried(bombsLeft);

        int seq;
        if (net != null)
        {
            ClaimNet();
            net.syncBombSeq = net.syncBombSeq + 1;
            net.syncBombPos = pos;
            net.syncBombVel = vel;
            net.syncBombTime = Networking.GetServerTimeInSeconds();
            seq = net.syncBombSeq;
        }
        else
        {
            seq = lastBombSeq + 1;
        }
        lastBombSeq = seq;   // the owner never replays its own bombs

        SpawnBomb(seq, pos, vel, 0f);
        if (debugLog) Debug.Log("[Dropper] bomb released, " + bombsLeft + " left");

        // out of bombs: harmless from now on, climb and trail (SetState also syncs the bomb)
        if (bombsLeft <= 0) BeginTrail();
        else SyncNow();
    }

    // ---------- Trail ----------

    void DoTrail(float dt)
    {
        if (!hasTarget)
        {
            StartPatrol(lastSeenPos);
            return;
        }

        unseenTimer += dt;
        if (unseenTimer >= loseSightTime)
        {
            StartPatrol(lastSeenPos);
            return;
        }

        bool seenNow = visibleCount >= spotMinPoints;
        Vector3 center = seenNow ? targetFeet : lastSeenPos;
        Vector3 centerVel = seenNow ? targetVel : Vector3.zero;

        // a small circle around the player: the goal point runs along the circle and moves with the player
        float r = Mathf.Max(trailRadius, 0.5f);
        trailAngle += (trailOrbitSpeed / r) * dt;
        float s = Mathf.Sin(trailAngle);
        float c = Mathf.Cos(trailAngle);
        Vector3 goal = center + new Vector3(s, 0f, c) * r;
        Vector3 goalVel = centerVel + new Vector3(c, 0f, -s) * trailOrbitSpeed;
        TrackPoint(goal, goalVel, trailSpeed, dt);

        if (seenNow) ReportTick(dt);
    }

    // ================= horizontal movement =================

    // Constant speed straight at a point (Travel, Patrol).
    void SeekPoint(Vector3 goal, float speed, float dt)
    {
        Vector3 to = goal - transform.position;
        to.y = 0f;
        float d = to.magnitude;
        Vector3 desired = Vector3.zero;
        if (d > 0.01f) desired = to / d * speed;
        MoveHorizontal(desired, dt);
    }

    // Follow a (moving) goal: the goal's own velocity plus a pull toward the goal, limited to maxSpeed.
    // With the player's velocity as feed-forward the drone stays right above a running player instead of lagging behind.
    void TrackPoint(Vector3 goal, Vector3 goalVel, float maxSpeed, float dt)
    {
        Vector3 to = goal - transform.position;
        to.y = 0f;
        Vector3 desired = goalVel + to * trackGain;
        desired.y = 0f;
        float m = desired.magnitude;
        if (m > maxSpeed) desired = desired / m * maxSpeed;
        MoveHorizontal(desired, dt);
    }

    void MoveHorizontal(Vector3 desired, float dt)
    {
        horizVel = Vector3.MoveTowards(horizVel, desired, acceleration * dt);
        transform.position = transform.position + horizVel * dt;
    }

    // ================= target selection (owner only) =================

    // Who may be targeted: playing AND alive roster players, or everyone with debugKillAnyTarget.
    // Fills 'candidates' and returns how many.
    int CollectTargets()
    {
        if (debugKillAnyTarget)
        {
            int count = VRCPlayerApi.GetPlayerCount();
            VRCPlayerApi.GetPlayers(candidates);
            return Mathf.Min(count, candidates.Length);
        }

        if (roster == null)
        {
            VRCPlayerApi lp = Networking.LocalPlayer;
            if (!Utilities.IsValid(lp)) return 0;
            candidates[0] = lp;
            return 1;
        }

        return roster.GetTargets(candidates);
    }

    // Keeps the locked target's position fresh every frame, and measures its horizontal velocity.
    // (dt = 0 only refreshes the position; ScanTargets does that.)
    void RefreshTargetPosition(float dt)
    {
        if (Utilities.IsValid(targetPlayer))
        {
            Vector3 feet = targetPlayer.GetPosition();

            if (velTrackId != targetId)
            {
                // new target: no velocity yet
                velTrackId = targetId;
                targetVel = Vector3.zero;
            }
            else if (dt > 0.0001f)
            {
                Vector3 v = (feet - targetFeet) / dt;
                v.y = 0f;
                if (v.magnitude > 15f) v = Vector3.zero;   // teleport (cemetery, respawn): not running
                float k = 1f - Mathf.Exp(-targetVelSmoothing * dt);
                targetVel = Vector3.Lerp(targetVel, v, k);
            }

            targetFeet = feet;
            Vector3 head = targetPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
            targetBodyHeight = Mathf.Max(head.y - targetFeet.y, 0.3f);
            hasTarget = true;
        }
        else
        {
            targetPlayer = null;
            hasTarget = false;
            velTrackId = -1;
            targetVel = Vector3.zero;
        }
    }

    // Runs at visionInterval. Picks the target:
    //  - a visible current target stays the target (sticky)
    //  - otherwise the nearest visible eligible player
    //  - if nobody is visible the current target is kept (we go to the last seen spot),
    //    unless it died / left / stopped playing.
    void ScanTargets()
    {
        int n = CollectTargets();

        VRCPlayerApi chosen = null;
        int chosenCount = 0;
        Vector3 chosenFeet = Vector3.zero;
        float chosenDist = 100000f;
        bool chosenIsCurrent = false;
        bool currentEligible = false;
        Vector3 myPos = flyingGroup.position;

        for (int i = 0; i < n; i++)
        {
            VRCPlayerApi p = candidates[i];
            if (!Utilities.IsValid(p)) continue;

            bool isCurrent = Utilities.IsValid(targetPlayer) && p.playerId == targetId;
            if (isCurrent) currentEligible = true;

            Vector3 feet = p.GetPosition();
            Vector3 head = p.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
            int count = CountVisiblePoints(feet, head);
            if (count < spotMinPoints) continue;

            float dist = HorizDistance(myPos, feet);
            bool better = isCurrent || (!chosenIsCurrent && dist < chosenDist);
            if (!better) continue;

            chosen = p;
            chosenCount = count;
            chosenFeet = feet;
            chosenDist = dist;
            chosenIsCurrent = isCurrent;
        }

        if (chosen != null)
        {
            targetPlayer = chosen;
            targetId = chosen.playerId;
            visibleCount = chosenCount;
            lastSeenPos = chosenFeet;
            unseenTimer = 0f;
            RefreshTargetPosition(0f);
            return;
        }

        // nobody visible
        visibleCount = 0;
        if (Utilities.IsValid(targetPlayer) && !currentEligible)
        {
            targetPlayer = null;   // died, left, or stopped playing
            hasTarget = false;
            velTrackId = -1;
            targetVel = Vector3.zero;
        }
    }

    // ================= vision =================

    // How many of feet / chest / head this drone can see.
    int CountVisiblePoints(Vector3 feet, Vector3 head)
    {
        if (debugIgnoreVision) return 3;

        Vector3 from = (eye != null) ? eye.position : flyingGroup.position;
        Vector3 footPt = feet + Vector3.up * 0.2f;
        Vector3 chestPt = Vector3.Lerp(feet, head, 0.5f);

        int count = 0;
        if (CanSeePoint(from, footPt)) count++;
        if (CanSeePoint(from, chestPt)) count++;
        if (CanSeePoint(from, head)) count++;
        return count;
    }

    // Inside the view cone (the eye points straight down), in range, and no obstacle in between.
    bool CanSeePoint(Vector3 from, Vector3 point)
    {
        Transform e = (eye != null) ? eye : flyingGroup;
        Vector3 d = point - from;
        float dist = d.magnitude;
        if (dist < 0.01f) return true;
        if (dist > viewRange) return false;

        Vector3 dir = d / dist;
        Vector3 axis = (eye != null) ? e.forward : Vector3.down;
        if (Vector3.Angle(axis, dir) > viewAngle * 0.5f) return false;

        RaycastHit hit;
        // stop a little short so the ground right at the player's feet never counts as cover
        float castDist = Mathf.Max(dist - 0.15f, 0.01f);
        bool blocked = Physics.Raycast(from, dir, out hit, castDist, obstacleMask, QueryTriggerInteraction.Ignore);
        if (debugDrawRays) Debug.DrawLine(from, point, blocked ? Color.red : Color.green, visionInterval);
        return !blocked;
    }

    // ================= bombs (every client) =================

    GameObject BombVisual(int i)
    {
        if (bombObjects == null || i < 0 || i >= bombObjects.Length) return null;
        return bombObjects[i];
    }

    GameObject BombEffect(int i)
    {
        if (bombEffects == null || i < 0 || i >= bombEffects.Length) return null;
        return bombEffects[i];
    }

    void ShowCarried(int n)
    {
        if (carriedBombs == null) return;
        for (int i = 0; i < carriedBombs.Length; i++)
        {
            if (carriedBombs[i] != null) carriedBombs[i].SetActive(i < n);
        }
    }

    // Starts a bomb in pool slot (seq % poolSize). catchUp = seconds it has already been falling (remote clients).
    void SpawnBomb(int seq, Vector3 pos, Vector3 vel, float catchUp)
    {
        int i = seq % poolSize;
        if (i < 0) i += poolSize;

        bombActive[i] = true;
        bombPos[i] = pos;
        bombVel[i] = vel;
        bombAge[i] = 0f;
        bombCatchUp[i] = catchUp;

        GameObject v = BombVisual(i);
        if (v != null)
        {
            v.transform.position = pos;
            v.SetActive(true);
        }
    }

    // Steps every falling bomb. Contact with the world or with THIS client's own player = explosion.
    void UpdateBombs(float dt)
    {
        if (dt <= 0f || bombActive == null) return;

        Vector3 g = new Vector3(0f, -bombGravity, 0f);

        for (int i = 0; i < poolSize; i++)
        {
            if (!bombActive[i]) continue;

            float step = dt + bombCatchUp[i];
            bombCatchUp[i] = 0f;
            bombAge[i] += step;

            Vector3 p0 = bombPos[i];
            Vector3 v0 = bombVel[i];
            Vector3 p1 = p0 + v0 * step + g * (0.5f * step * step);
            Vector3 v1 = v0 + g * step;

            // 1) the world (ground, roofs, walls, trees)
            Vector3 end = p1;
            bool hitWorld = false;
            Vector3 seg = p1 - p0;
            float len = seg.magnitude;
            if (len > 0.0001f)
            {
                RaycastHit hit;
                if (Physics.Raycast(p0, seg / len, out hit, len, obstacleMask, QueryTriggerInteraction.Ignore))
                {
                    hitWorld = true;
                    end = hit.point;
                }
            }

            // 2) this client's own player, anywhere along the way to that point
            if (BodyContact(p0, end))
            {
                ExplodeBomb(i, contactPoint);
                continue;
            }

            if (hitWorld)
            {
                ExplodeBomb(i, end);
                continue;
            }

            if (bombAge[i] > bombMaxLifetime)
            {
                bombActive[i] = false;
                GameObject old = BombVisual(i);
                if (old != null) old.SetActive(false);
                continue;
            }

            bombPos[i] = p1;
            bombVel[i] = v1;

            GameObject vis = BombVisual(i);
            if (vis != null)
            {
                vis.transform.position = p1;
                if (bombAlignToVelocity && v1.sqrMagnitude > 0.01f) vis.transform.rotation = Quaternion.LookRotation(v1, Vector3.up);
            }
        }
    }

    // Does the bomb's path p0 -> p1 touch the local player's body? Sets contactPoint.
    bool BodyContact(Vector3 p0, Vector3 p1)
    {
        if (!CanLocalBeHit()) return false;

        VRCPlayerApi lp = Networking.LocalPlayer;
        if (!Utilities.IsValid(lp)) return false;

        Vector3 feet = lp.GetPosition();
        Vector3 head = lp.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
        float bodyHeight = Mathf.Max(head.y - feet.y, 0.3f);

        // quick reject: the whole path is above or below the body
        if (Mathf.Min(p0.y, p1.y) > feet.y + bodyHeight + bombHitRadius) return false;
        if (Mathf.Max(p0.y, p1.y) < feet.y - bombHitRadius) return false;

        float len = Vector3.Distance(p0, p1);
        int n = Mathf.Clamp(Mathf.CeilToInt(len / 0.25f), 1, 24);
        for (int s = 0; s <= n; s++)
        {
            Vector3 p = Vector3.Lerp(p0, p1, (float)s / (float)n);
            if (DistToBody(p, feet, bodyHeight) < bombHitRadius)
            {
                contactPoint = p;
                return true;
            }
        }
        return false;
    }

    // Effect on every client; the blast check only concerns THIS client's own player.
    void ExplodeBomb(int i, Vector3 point)
    {
        bombActive[i] = false;

        GameObject v = BombVisual(i);
        if (v != null) v.SetActive(false);

        GameObject fx = BombEffect(i);
        if (fx != null)
        {
            fx.transform.position = point;
            fx.SetActive(false);
            fx.SetActive(true);
        }

        if (debugLog) Debug.Log("[Dropper] bomb exploded at " + point);

        VRCPlayerApi lp = Networking.LocalPlayer;
        if (!Utilities.IsValid(lp) || !CanLocalBeHit()) return;

        Vector3 feet = lp.GetPosition();
        Vector3 head = lp.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
        float bodyHeight = Mathf.Max(head.y - feet.y, 0.3f);
        float dist = DistToBody(point, feet, bodyHeight);
        if (dist > bombKillRadius) return;
        if (dist <= bombCoreRadius || BlastReaches(point, feet, bodyHeight)) KillLocalPlayer("bomb");
    }

    // true if at least one body point (head, chest, shins) has a clear line to the blast
    bool BlastReaches(Vector3 pos, Vector3 feet, float bodyHeight)
    {
        Vector3 origin = pos + Vector3.up * 0.5f;
        Vector3 flat = feet - origin;
        flat.y = 0f;
        Vector3 side = flat.sqrMagnitude > 0.0001f
            ? Vector3.Cross(Vector3.up, flat.normalized) * blastHalfWidth
            : Vector3.right * blastHalfWidth;

        for (int s = -1; s <= 1; s++)
        {
            for (int i = 0; i < 3; i++)
            {
                Vector3 pt = feet + Vector3.up * (bodyHeight * (0.9f - i * 0.375f)) + side * s;
                Vector3 d = pt - origin;
                float len = d.magnitude;
                if (len < 0.05f) return true;
                RaycastHit hit;
                if (!Physics.Raycast(origin, d / len, out hit, len, obstacleMask, QueryTriggerInteraction.Ignore)) return true;
            }
        }
        return false;
    }

    bool CanLocalBeHit()
    {
        if (debugKillAnyTarget) return true;
        if (localState == null) return true;
        return localState.IsTargetable();
    }

    void KillLocalPlayer(string why)
    {
        if (localState != null && localState.IsTargetable())
        {
            localState.Kill();
            Debug.Log("[Dropper] local player KILLED (" + why + ")");
        }
        else
        {
            Debug.Log("[Dropper] (debug) local player would have been killed (" + why + ")");
        }
    }

    // ================= being shot / exploded =================

    // Owner only. Everyone else gets it through the synced explosion counter. Visual only: it never hurts a player.
    void Explode()
    {
        if (!aiActive) return;
        if (state == STATE_EXPLODED || state == STATE_PARKED) return;

        Vector3 pos = flyingGroup.position;
        state = STATE_EXPLODED;
        explodeTimer = debugRespawnDelay;
        horizVel = Vector3.zero;
        targetPlayer = null;
        hasTarget = false;
        velTrackId = -1;
        targetVel = Vector3.zero;

        if (debugLog) Debug.Log("[Dropper] EXPLODED: shot by player");

        if (net != null)
        {
            net.syncExplosionSeq = net.syncExplosionSeq + 1;
            net.syncExplosionPos = pos;
            lastExplosionSeq = net.syncExplosionSeq;   // the owner never replays its own explosion
        }
        ExplosionLocal(pos);
        SyncNow();
    }

    // Runs on EVERY client: effect and hide the drone. No kill.
    void ExplosionLocal(Vector3 pos)
    {
        if (explosionEffect != null)
        {
            explosionEffect.transform.position = pos;
            explosionEffect.SetActive(false);
            explosionEffect.SetActive(true);
        }

        flyingGroup.gameObject.SetActive(false);
    }

    void UpdateExploded(float dt)
    {
        if (captain != null || !debugAutoRespawn) return;
        explodeTimer -= dt;
        if (explodeTimer <= 0f) ResetToLaunch(launchPos);
    }

    // ================= network sync =================

    // Owner -> everyone. Called on every state change, every bomb release and (rate limited) when altitude or yaw change.
    void SyncNow()
    {
        if (!aiActive) return;

        if (net == null) return;
        ClaimNet();

        float yaw = flyingGroup.eulerAngles.y;
        net.syncState = state;
        net.syncAlt = currentAlt;
        net.syncYaw = yaw;
        net.syncTravelDest = travelDest;
        net.syncBombsLeft = bombsLeft;

        lastSentAlt = currentAlt;
        lastSentYaw = yaw;
        syncTimer = syncInterval;
        net.Push();
    }

    void MaybeSync(float dt)
    {
        syncTimer -= dt;
        if (syncTimer > 0f) return;

        float yaw = flyingGroup.eulerAngles.y;
        float yawDiff = Mathf.Abs(Mathf.DeltaAngle(yaw, lastSentYaw));
        if (Mathf.Abs(currentAlt - lastSentAlt) > 0.02f || yawDiff > 2f) SyncNow();
        else syncTimer = 0f;   // nothing changed, check again next frame
    }

    // Other clients receive the owner's state here (DropperNetSync calls this from its OnDeserialization).
    public void OnSyncReceived()
    {
        if (aiActive || net == null) return;

        state = net.syncState;
        travelDest = net.syncTravelDest;
        bombsLeft = net.syncBombsLeft;
        ShowCarried(bombsLeft);

        if (!gotFirstSync)
        {
            // late joiner: adopt the current picture, do not replay old explosions or bombs
            gotFirstSync = true;
            lastExplosionSeq = net.syncExplosionSeq;
            lastBombSeq = net.syncBombSeq;
            currentAlt = net.syncAlt;
            flyingGroup.rotation = Quaternion.Euler(0f, net.syncYaw, 0f);
        }
        else
        {
            if (net.syncExplosionSeq != lastExplosionSeq)
            {
                lastExplosionSeq = net.syncExplosionSeq;
                ExplosionLocal(net.syncExplosionPos);
            }

            if (net.syncBombSeq != lastBombSeq)
            {
                lastBombSeq = net.syncBombSeq;
                // the bomb was released a moment ago: start it already that far into its fall
                float late = (float)(Networking.GetServerTimeInSeconds() - net.syncBombTime);
                if (late < 0f) late = 0f;
                if (late < bombMaxLifetime) SpawnBomb(net.syncBombSeq, net.syncBombPos, net.syncBombVel, late);
            }
        }

        flyingGroup.gameObject.SetActive(state != STATE_EXPLODED);
    }

    // ================= altitude + placement =================

    void LateUpdate()
    {
        float dt = Time.deltaTime;

        if (!aiActive)
        {
            LateUpdateRemote(dt);
            return;
        }

        if (state == STATE_EXPLODED) return;

        Vector3 rootPos = transform.position;
        float groundY = rootPos.y;               // root Y = ground height at the last launch

        UpdateAltitude(GetTargetAltitude(groundY), dt);

        float floorY = groundY + hardFloorClearance;
        if (currentAlt < floorY)
        {
            currentAlt = floorY;
            if (altVel < 0f) altVel = 0f;
        }

        flyingGroup.position = new Vector3(rootPos.x, currentAlt, rootPos.z);

        // facing (yaw only, cosmetic): along the motion
        Vector3 look = horizVel;
        look.y = 0f;
        if (look.sqrMagnitude > 0.25f)
        {
            Quaternion want = Quaternion.LookRotation(look.normalized, Vector3.up);
            flyingGroup.rotation = Quaternion.RotateTowards(flyingGroup.rotation, want, turnSpeed * dt);
        }

        UpdateTilt(dt);
        MaybeSync(dt);
    }

    // Other clients: no AI. The root comes from Object Sync; altitude and yaw come from the synced values.
    void LateUpdateRemote(float dt)
    {
        if (state == STATE_EXPLODED || net == null) return;

        Vector3 rootPos = transform.position;

        float k = 1f - Mathf.Exp(-remoteAltSmoothing * dt);
        currentAlt = Mathf.Lerp(currentAlt, net.syncAlt, k);

        float floorY = rootPos.y + hardFloorClearance;
        if (currentAlt < floorY) currentAlt = floorY;

        flyingGroup.position = new Vector3(rootPos.x, currentAlt, rootPos.z);

        Quaternion want = Quaternion.Euler(0f, net.syncYaw, 0f);
        flyingGroup.rotation = Quaternion.RotateTowards(flyingGroup.rotation, want, turnSpeed * 2f * dt);

        UpdateTilt(dt);
    }

    // The altitude (world Y) to aim for.
    //  Parked: just above the ground. Travel / Patrol / Attack approach: cruiseHeight above the reference ground.
    //  Attack, close to the player: attackHeight above the player's feet. Trail: trailHeight above the player's feet.
    float GetTargetAltitude(float groundY)
    {
        if (state == STATE_PARKED) return groundY + minClearance;

        float feetY = hasTarget ? targetFeet.y : groundY;
        if (state == STATE_ATTACK && attackLow) return feetY + attackHeight;
        if (state == STATE_TRAIL) return feetY + trailHeight;
        return referenceGroundY + cruiseHeight;
    }

    // Helicopter feel: slow heavy climb, quicker descent, braking so it eases into the target.
    void UpdateAltitude(float target, float dt)
    {
        float dy = target - currentAlt;
        float maxV = (dy < 0f) ? maxDescendSpeed : maxClimbSpeed;
        float a = (dy < 0f) ? descendAccel : climbAccel;

        float brakeSpeed = Mathf.Sqrt(2f * a * Mathf.Abs(dy));
        float desiredV = Mathf.Sign(dy) * Mathf.Min(maxV, brakeSpeed);

        altVel = Mathf.MoveTowards(altVel, desiredV, a * dt);
        currentAlt += altVel * dt;
    }

    // ================= tilt animation (cosmetic) =================

    // Feeds the tilt Animator on EVERY client. The speed is measured from how the Flying Group actually moved,
    // so the owner and the other clients all see the same thing and nothing has to be synced.
    void UpdateTilt(float dt)
    {
        if (tiltAnimator == null) return;

        Vector3 pos = flyingGroup.position;
        float yaw = flyingGroup.eulerAngles.y;
        if (!tiltInit)
        {
            tiltInit = true;
            lastFlyPos = pos;
            lastFlyYaw = yaw;
            return;
        }

        // a teleport (reset to launch, or it just became visible again): do not tilt for the jump
        if ((pos - lastFlyPos).magnitude > 5f)
        {
            lastFlyPos = pos;
            lastFlyYaw = yaw;
            return;
        }

        float safeDt = Mathf.Max(dt, 0.0001f);
        Vector3 worldVel = (pos - lastFlyPos) / safeDt;
        lastFlyPos = pos;

        // how fast the drone is turning (degrees per second, + = turning right seen from above)
        float yawRate = Mathf.DeltaAngle(lastFlyYaw, yaw) / safeDt;
        lastFlyYaw = yaw;

        Vector3 localVel = flyingGroup.InverseTransformDirection(worldVel);
        float fwdTarget = Mathf.Clamp(localVel.z / tiltFullSpeed, -1f, 1f);

        // strafing leans sideways, and a turn adds a bank into the turn on top of it
        float sideTarget = localVel.x / tiltFullSpeed;
        float flatSpeed = Mathf.Sqrt(worldVel.x * worldVel.x + worldVel.z * worldVel.z);
        float speedFactor = Mathf.Clamp01(flatSpeed / Mathf.Max(tiltTurnMinSpeed, 0.1f));
        sideTarget += (yawRate / Mathf.Max(tiltFullTurnRate, 1f)) * speedFactor;
        sideTarget = Mathf.Clamp(sideTarget, -1f, 1f);

        float k = 1f - Mathf.Exp(-tiltSmoothing * dt);
        tiltFwd = Mathf.Lerp(tiltFwd, fwdTarget, k);
        tiltSide = Mathf.Lerp(tiltSide, sideTarget, k);

        tiltAnimator.SetFloat(forwardParam, tiltFwd);
        tiltAnimator.SetFloat(sideParam, tiltSide);
    }

    void ResetTilt()
    {
        tiltInit = false;
        tiltFwd = 0f;
        tiltSide = 0f;
    }

    // ================= helpers =================

    float HorizDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    // Distance from a point to a player's body, treated as a vertical line from feet to head.
    float DistToBody(Vector3 p, Vector3 feet, float bodyHeight)
    {
        float y = Mathf.Clamp(p.y, feet.y, feet.y + bodyHeight);
        Vector3 closest = new Vector3(feet.x, y, feet.z);
        return Vector3.Distance(p, closest);
    }
}
