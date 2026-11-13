using UdonSharp;
using UnityEngine;
using VRC.Udon;
using UnityEngine.AI;
using VRC.SDKBase;
using VRC.SDK3.Components;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// Drone with a state machine, networked.
///
/// STATES
/// PARKED   : inert at the launch point until commanded (the manager decides when; difficulty 0 = never dispatched).
/// TRAVEL   : fly to a commanded destination at travelHeight. Spots players on the way.
/// PATROL   : circle a center point clockwise, radius grows over time, at cruiseHeight.
/// CHASE    : hunts the LAST SEEN position (not the true one). Reports sightings. Lost for loseSightTime -> PATROL around it.
/// DIVE     : phase of CHASE. Committed glide at the player's feet. Locked destination, so the player can dodge.
///            explodeOnMissedDive ON : the glide ends in the ground and the drone explodes.
///            explodeOnMissedDive OFF: the glide skims diveEndHeight above the feet, then recovers and retries.
/// RUSH     : phase of CHASE (no separate state, nothing extra to sync). When the drone cannot dive (indoors, too close,
///            no clear line, ...) it stops hovering, flies straight at the player and detonates inside detonateRadius.
///            The blast kills everything inside explosionKillRadius, so it never has to touch anything.
/// EXPLODED : effect, hidden, waits for the manager to call ResetToLaunch().
///
/// NETWORK MODEL
/// - The drone's owner runs ALL the AI (NavMeshAgent, states, vision, target choice). The owner follows the Game Host.
/// - Every other client only DISPLAYS: agent disabled, root position comes from VRC Object Sync,
///   state / altitude / yaw / dive data / explosions come from the synced variables on the DroneNetSync child object.
/// - Hits are decided on the VICTIM's client (no latency unfairness): contact during a dive and the explosion
///   radius are checked against the local player, which then calls LocalPlayerState.Kill() on itself.
/// - Shots: whoever's client registers the hit calls OnShot(); it is forwarded to the owner.
///
/// Horizontal movement = NavMeshAgent on this root. Vertical = altitude controller on the Flying Group (world Y).
/// </summary>
// NoVariableSync (network events + ownership, no variables): VRC Object Sync cannot share an object with a Manual behaviour.
// All synced variables live on the DroneNetSync child object (field 'net').
[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class DroneChaser : UdonSharpBehaviour
{
    [Header("References")]
    public NavMeshAgent agent;          // on this root object
    public Transform flyingGroup;       // child holding mesh + collider; its world Y is driven here
    public Transform eye;               // child "camera" point, pitched DOWN (about 20-45 degrees)
    public LayerMask obstacleMask;      // walls, pillars, terrain, buildings. Exclude player layers.
    public GameObject explosionEffect;  // optional. Must NOT be a child of flyingGroup (that gets hidden)

    [Header("Multiplayer")]
    public GameManager gameManager;     // the drone's owner follows the Game Host. Empty = no ownership logic (solo test)
    public PlayerRoster roster;         // who may be targeted (GetTargets). Empty = local player only
    public LocalPlayerState localState; // how THIS client kills its own player
    public VRCObjectSync objectSync;    // optional, on this root: avoids a visible slide after a reset
    public DroneNetSync net;            // REQUIRED: child object holding the synced variables (Manual sync)
    public float syncInterval = 0.1f;   // max rate of altitude / yaw updates
    public float remoteAltSmoothing = 10f; // other clients: how fast the altitude follows the synced value
    public OpForCaptain captain;        // the manager. When set it launches, re-sends and respawns this drone (debug auto-dispatch / auto-respawn are ignored)

    [Header("Tilt animation (cosmetic, runs on every client)")]
    public Animator tiltAnimator;          // Animator on TiltPivot. Empty = no tilt
    public string forwardParam = "VelForward"; // float parameter: -1 (backing up) to +1 (full speed forward)
    public string sideParam = "VelSide";       // float parameter: -1 (moving left) to +1 (moving right)
    public float tiltFullSpeed = 12f;      // speed (m/s) that counts as full tilt (the dive is 12)
    public float tiltSmoothing = 8f;       // higher = snappier
    public float tiltFullTurnRate = 120f;  // yaw rate (deg/s) that counts as a full bank into a turn
    public float tiltTurnMinSpeed = 3f;    // speed (m/s) for full turn banking; slower = less bank (hovering and spinning in place does not lean)

    [Header("Altitude (world Y)")]
    public float referenceGroundY = 0f; // world Y of your flat ground; heights are measured from this
    public float cruiseHeight = 3f;     // Patrol and Chase
    public float travelHeight = 8f;     // Travel only
    public float minClearance = 1f;     // soft: target altitude is never below ground + this
    public float hardFloorClearance = 0.25f; // hard: never below ground + this
    public float maxClimbSpeed = 4f;
    public float climbAccel = 4f;
    public float maxDescendSpeed = 10f;
    public float descendAccel = 6f;

    [Header("Indoor / ceiling look-ahead")]
    public bool useCeilingCheck = true;
    public float indoorHeight = 1.2f;         // preferred height above the floor when there is a ceiling to clear
    public float indoorMinClearance = 0.4f;   // lowest it flies above the floor indoors
    public float ceilingMargin = 0.3f;        // stay this far below the ceiling / door lintel
    public float ceilingCheckInterval = 0.2f; // how often the check runs (each drone has its own random offset)
    public float ceilingSampleSpacing = 0.8f; // distance between checked points along the way ahead
    public float ceilingSphereRadius = 0.4f;  // thick upward cast, so a thin lintel is never missed (about half the spacing)
    public float ceilingStartHeight = 0.5f;   // casts start this high above the floor (clears the floor itself)
    public float ceilingLookAheadTime = 0.5f; // seconds of travel to look ahead, plus the time needed to descend
    public float ceilingMinLook = 2f;         // always look at least this far (meters)
    public int ceilingMaxSamples = 20;        // cap on checked points per run (look-ahead = spacing x this)
    public float ceilingRaiseDelay = 0.4f;    // after a ceiling is gone, wait this long before climbing again
    public float diveMinAngleIndoor = 5f;     // indoors the dive is a low, fast dash: much shallower minimum angle
    public float diveMaxDistanceIndoor = 7f;  // ...and only from this close
    public bool debugDrawCeiling;             // draw the ceiling checks in the Scene view (cyan = ceiling, green = open)

    [Header("Movement")]
    public float travelSpeed = 15f;
    public float patrolSpeed = 3f;
    public float chaseSpeed = 8f;
    public float chaseAccel = 8f;          // agent acceleration for every state except Dive
    public float turnSpeed = 180f;         // degrees per second
    public float arriveDistance = 2f;      // Travel: close enough to the destination
    public float travelStuckTime = 8f;     // Travel: give up and patrol if no progress this long

    [Header("Patrol (clockwise ring, growing)")]
    public float patrolStartRadius = 1f;
    public float patrolRadiusGrowth = 0.5f;   // meters per second
    public float patrolMaxRadius = 50f;
    public float patrolStepDistance = 6f;     // roughly how far apart ring waypoints are
    public float patrolArriveDistance = 1.5f;
    public float patrolStuckTime = 2f;        // skip a waypoint after this long without progress
    public float patrolSampleRadius = 4f;     // NavMesh.SamplePosition search radius

    [Header("Awareness")]
    public int spotMinPoints = 1;          // visible points (of feet/chest/head) needed to SPOT a player
    public float loseSightTime = 4f;       // unseen this long -> Patrol around the last seen position
    public float spotReactionTime = 1f;    // no dive for this long after first spotting
    public float reportInterval = 2f;      // Chase: how often it reports the player's location
    public float lostStopDistance = 1.5f;  // when the player is hidden, go this close to the last seen spot

    [Header("Chase")]
    public float chaseStopDistance = 4f;   // hover (horizontal) this far from the player while waiting to dive
    public float retreatDistance = .5f;     // closer than this: back away to get a diving angle
    public float retreatStep = 3f;
    public float repathInterval = 0.25f;

    [Header("Vision")]
    public float viewAngle = 90f;          // total cone angle
    public float viewRange = 50f;
    public float visionInterval = 0.25f;
    public int minVisiblePoints = 2;       // to DIVE: feet + at least one more (of feet, chest, head)

    [Header("Dive")]
    public float diveSpeed = 15f;
    public float diveAccel = 12f;
    public float diveMinAngle = 0f;       // glide angle limits, degrees below horizontal
    public float diveMaxAngle = 80f;
    public float diveFrontAngle = 40f;     // player must be within this angle of the drone's facing to start a dive
    public float diveTurnRate = 45f;          // degrees per second the glide may swing toward the player. 0 = fully committed (old behaviour)
    public float diveSteerStopDistance = 2f;  // no more steering when the locked point is this close (metres)
    public float diveEndHeight = 0.4f;     // glide end height above feet (only when explodeOnMissedDive is OFF)
    public float diveOvershoot = 5f;       // flies this far past the locked point
    public float diveMaxTime = 3f;
    public float diveCooldown = 1f;
    public float hitRadius = 0.5f;         // contact distance to a player's body (drone size + player size). The PREFAB value overrides this default
    public float bodyRadius = 0.2f;        // for the sphere cast checks
    public bool explodeOnMissedDive = true;
    public float explosionKillRadius = 5f; // a player inside this distance of an explosion is killed
    public float explosionCoreRadius = .5f;   // inside this the blast kills through walls; out to explosionKillRadius it needs line of sight
    public float blastHalfWidth = 0.35f;   // sideways ray spread; wider than a body so thin poles can't hide a player

    [Header("Rush (fallback when it cannot dive)")]
    public float rushDelay = 1f;           // cannot dive for this long while seeing the player -> rush
    public bool rushIndoors = true;        // under a ceiling: skip the dive, rush at once (a locked glide cannot turn corners)
    public float rushSpeed = 8f;
    public float detonateRadius = 1f;      // rush: explode this close to the player's body (keep it below explosionKillRadius)

    [Header("Debug (set these in the editor BEFORE building; builds have no inspector)")]
    public bool debugDisableDive;          // never dive
    public bool debugIgnoreVision;         // drone always sees every eligible player (cone and obstacles ignored)
    public bool debugLog;                  // log state changes and events
    public bool debugDrawRays;             // draw vision rays in the Scene view
    public bool debugDiveReasons;          // log WHY a dive did not start (throttled)
    public bool debugKillAnyTarget;        // target and kill ANY player, not only players in roster slots
    public bool debugAutoDispatch = false;  // Parked drone dispatches itself. Turn OFF when the manager sends commands.
    public float debugDispatchDelay = 1f;
    public Transform debugTravelTarget;    // optional: auto-dispatch travels here, otherwise it patrols where it stands
    public bool debugAutoRespawn = false;   // Exploded drone resets itself. Turn OFF when the manager does it.
    public float debugRespawnDelay = 3f;
    public bool debugTestShot;             // tick in play mode to simulate a gun hit

    private const int STATE_PARKED = 0;
    private const int STATE_TRAVEL = 1;
    private const int STATE_PATROL = 2;
    private const int STATE_CHASE = 3;
    private const int STATE_DIVE = 4;
    private const int STATE_EXPLODED = 5;

    private const int REASON_SHOT = 0;
    private const int REASON_PLAYER = 1;
    private const int REASON_GROUND = 2;

    // (the synced variables live on the DroneNetSync object, see 'net')

    // ---- network bookkeeping ----
    private bool aiActive;              // true on the owner only
    private bool initialized;
    private bool gotFirstSync;
    private int lastExplosionSeq;
    private float followHostTimer;
    private float syncTimer;
    private float lastSentAlt;
    private float lastSentYaw;
    private float shotLockUntil;     // shooter's client: one hit per blast (a shotgun lands up to 9 pellets at once)
    private bool localContactSent;

    // ---- state (on non-owners 'state' mirrors the synced state) ----
    private int state;
    private float currentAlt;
    private float altVel;
    private float cooldown;
    private float repathTimer;
    private float visionTimer;
    private float dispatchTimer;
    private float explodeTimer;
    private float reportTimer;
    private Vector3 lastRootPos;

    // tilt animation
    private bool tiltInit;
    private Vector3 lastFlyPos;
    private float tiltFwd;
    private float tiltSide;
    private float lastFlyYaw;

    // ceiling look-ahead (owner only)
    private float ceilingTimer;
    private float ceilingLowestY = 100000f;   // lowest ceiling underside (world Y) ahead or overhead; 100000 = none
    private float ceilingRaiseTimer;
    private bool indoorNow;
    private Vector3 launchPos;

    // ---- targeting (owner only) ----
    private VRCPlayerApi[] candidates = new VRCPlayerApi[82];
    private VRCPlayerApi targetPlayer;
    private int targetId;
    private bool hasTarget;
    private Vector3 targetFeet;
    private float targetBodyHeight;
    private bool scanFeetVisible;

    // vision results for the current target (updated at visionInterval)
    private bool feetVisible;
    private int visibleCount;
    private Vector3 lastSeenPos;
    private float unseenTimer;

    // travel / progress tracking
    private Vector3 travelDest;
    private float progressBest;
    private float noProgressTimer;

    // patrol
    private Vector3 patrolCenter;
    private Vector3 patrolPoint;
    private float patrolAngle;     // radians, increasing = clockwise seen from above
    private float patrolTime;
    private float patrolHold;

    // dive
    private Vector3 diveDir;
    private Vector3 diveCommit;
    private float diveEndY;
    private float diveSlope;
    private float diveSteerTimer;
    private float diveTimer;
    private bool divePassed;
    private float nextReasonLog;

    // rush
    private float rushTimer;

    void Start()
    {
        if (agent == null) agent = GetComponent<NavMeshAgent>();
        launchPos = transform.position;
        lastRootPos = transform.position;
        currentAlt = flyingGroup.position.y;
        cooldown = 1f;
        ResetCeiling();
        ApplyOwnership();
    }

    // ================= ownership =================

    // Owner = runs the AI with the NavMeshAgent. Everyone else only displays.
    void ApplyOwnership()
    {
        bool owner = Networking.IsOwner(gameObject);
        if (initialized && owner == aiActive) return;
        bool first = !initialized;
        initialized = true;

        if (!owner)
        {
            aiActive = false;
            agent.enabled = false;
            return;
        }

        aiActive = true;
        ClaimNet();
        agent.enabled = true;
        agent.Warp(transform.position);   // re-bind to the navmesh at the current (synced) position
        lastRootPos = transform.position;

        // Continue from whatever the previous owner last showed ('state' mirrors it on non-owners).
        int adopt = first ? STATE_PARKED : state;
        if (adopt == STATE_EXPLODED)
        {
            state = STATE_EXPLODED;
            explodeTimer = debugRespawnDelay;
            agent.isStopped = true;
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

    // Manager -> drone (owner only). Ignored while hunting or exploded.
    public void CommandTravel(Vector3 destination)
    {
        if (!aiActive) return;
        if (state == STATE_CHASE || state == STATE_DIVE || state == STATE_EXPLODED) return;
        travelDest = destination;
        SetState(STATE_TRAVEL);
        agent.SetDestination(destination);
        ResetProgress();
    }

    // Manager -> drone (owner only), after an explosion (or any time). Puts the drone back at the launch point.
    // If agent.Warp is not exposed in Udon, use: agent.enabled = false; transform.position = pos; agent.enabled = true;
    // If FlagDiscontinuity does not compile, delete that line (the drone just slides for a moment on other clients).
    public void ResetToLaunch(Vector3 pos)
    {
        if (!aiActive) return;
        agent.Warp(pos);
        if (objectSync != null) objectSync.FlagDiscontinuity();
        lastRootPos = transform.position;
        flyingGroup.gameObject.SetActive(true);
        currentAlt = pos.y + minClearance;
        altVel = 0f;
        flyingGroup.position = new Vector3(pos.x, currentAlt, pos.z);
        ResetTilt();
        ResetCeiling();
        cooldown = diveCooldown;
        unseenTimer = 0f;
        visionTimer = 0f;
        targetPlayer = null;
        hasTarget = false;
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

    // Free = may take a new order: Travel or Patrol. Chase / Dive (attacking) and Exploded ignore orders.
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

    // Relay script on the Box Collider calls these, on whatever client registered the hit.
    public void OnShot()
    {
        if (Time.time < shotLockUntil) return;   // the pellets of one blast arrive within a few frames: count them once
        shotLockUntil = Time.time + 0.3f;
        if (aiActive) Explode(REASON_SHOT);
        else SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(NetShot));
    }

    public void OnHitGround()
    {
        if (aiActive) Explode(REASON_GROUND);
        else SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(NetGround));
    }

    public void OnBulletHit()
    {
        OnShot();
    }

    // Network events: these run on the owner.
    public void NetShot()
    {
        Explode(REASON_SHOT);
    }

    public void NetGround()
    {
        Explode(REASON_GROUND);
    }

    public void NetHitPlayer()
    {
        if (aiActive && state == STATE_DIVE) Explode(REASON_PLAYER);
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

        CheckLocalContact();   // every client checks its OWN player

        if (!aiActive) return; // other clients only display (see LateUpdate)

        if (state == STATE_EXPLODED) { UpdateExploded(dt); return; }
        if (state == STATE_PARKED) { UpdateParked(dt); return; }

        cooldown -= dt;

        RefreshTargetPosition();

        visionTimer -= dt;
        if (visionTimer <= 0f)
        {
            visionTimer = visionInterval;
            ScanTargets();
        }

        if (state == STATE_TRAVEL) DoTravel(dt);
        else if (state == STATE_PATROL) DoPatrol(dt);
        else if (state == STATE_CHASE) DoChase(dt);
        else if (state == STATE_DIVE) DoDive(dt);
    }

    // ================= states =================

    void SetState(int s)
    {
        state = s;

        if (s == STATE_PARKED)
        {
            agent.isStopped = true;
            dispatchTimer = debugDispatchDelay;
        }
        else if (s == STATE_TRAVEL)
        {
            agent.isStopped = false;
            agent.speed = travelSpeed;
            agent.acceleration = chaseAccel;
        }
        else if (s == STATE_PATROL)
        {
            agent.isStopped = false;
            agent.speed = patrolSpeed;
            agent.acceleration = chaseAccel;
        }
        else if (s == STATE_CHASE)
        {
            agent.isStopped = false;
            agent.speed = chaseSpeed;
            agent.acceleration = chaseAccel;
            repathTimer = 0f;
        }
        else if (s == STATE_DIVE)
        {
            agent.isStopped = false;
            agent.speed = diveSpeed;
            agent.acceleration = diveAccel;
        }

        if (debugLog) Debug.Log("[Drone] state -> " + StateName(s));
        SyncNow();
    }

    string StateName(int s)
    {
        if (s == STATE_PARKED) return "PARKED";
        if (s == STATE_TRAVEL) return "TRAVEL";
        if (s == STATE_PATROL) return "PATROL";
        if (s == STATE_CHASE) return "CHASE";
        if (s == STATE_DIVE) return "DIVE";
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

        // unreachable or blocked: search around the destination anyway
        if (NoProgress(d, dt, travelStuckTime)) StartPatrol(travelDest);
    }

    // ---------- Patrol ----------

    void StartPatrol(Vector3 center)
    {
        patrolCenter = center;
        patrolTime = 0f;
        patrolHold = 0f;

        // drop the old target so the next scan picks fresh
        targetPlayer = null;
        hasTarget = false;

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

        if (patrolHold > 0f)
        {
            patrolHold -= dt;
            if (patrolHold <= 0f) PickNextPatrolPoint();
            return;
        }

        float d = HorizDistance(transform.position, patrolPoint);
        if (d < patrolArriveDistance || NoProgress(d, dt, patrolStuckTime)) PickNextPatrolPoint();
    }

    // Next waypoint further clockwise along the (growing) ring. Unreachable points are skipped.
    void PickNextPatrolPoint()
    {
        float radius = Mathf.Min(patrolStartRadius + patrolRadiusGrowth * patrolTime, patrolMaxRadius);
        float step = Mathf.Clamp(patrolStepDistance / Mathf.Max(radius, 1f), 0.25f, 0.8f);

        for (int i = 0; i < 8; i++)
        {
            patrolAngle += step;   // + = clockwise seen from above (0 = +Z, 90 degrees = +X)
            Vector3 raw = patrolCenter + new Vector3(Mathf.Sin(patrolAngle), 0f, Mathf.Cos(patrolAngle)) * radius;
            raw.y = patrolCenter.y;

            if (TryFindPatrolPoint(raw))
            {
                agent.isStopped = false;
                agent.SetDestination(patrolPoint);
                ResetProgress();
                return;
            }
        }

        // every try failed: head to the center and try again shortly
        patrolPoint = patrolCenter;
        agent.isStopped = false;
        agent.SetDestination(patrolCenter);
        ResetProgress();
        patrolHold = 0.5f;
    }

    // Puts the nearest navmesh point into patrolPoint.
    // IF NavMesh.SamplePosition DOES NOT COMPILE in Udon: delete the whole if-block and use the fallback:
    //     patrolPoint = raw; return true;
    // DoPatrol's no-progress check then skips unreachable points by itself.
    bool TryFindPatrolPoint(Vector3 raw)
    {
        NavMeshHit navHit;
        if (NavMesh.SamplePosition(raw, out navHit, patrolSampleRadius, -1))
        {
            patrolPoint = navHit.position;
            return true;
        }
        return false;
    }

    // ---------- Awareness helpers ----------

    bool CheckSpotted()
    {
        if (hasTarget && visibleCount >= spotMinPoints)
        {
            StartChase();
            return true;
        }
        return false;
    }

    void StartChase()
    {
        lastSeenPos = targetFeet;
        unseenTimer = 0f;
        reportTimer = 0f;
        cooldown = Mathf.Max(cooldown, spotReactionTime);
        rushTimer = 0f;
        SetState(STATE_CHASE);
    }

    void ResetProgress()
    {
        progressBest = 100000f;
        noProgressTimer = 0f;
    }

    // True when we have not gotten meaningfully closer to the goal for 'limit' seconds.
    bool NoProgress(float distNow, float dt, float limit)
    {
        if (distNow < progressBest - 0.3f)
        {
            progressBest = distNow;
            noProgressTimer = 0f;
        }
        else
        {
            noProgressTimer += dt;
        }
        return noProgressTimer >= limit;
    }

    // Tells the Captain where a player was seen (it fills that player's location data).
    void ReportSighting(Vector3 pos)
    {
        if (debugLog) Debug.Log("[Drone] sighting reported at " + pos);
        if (captain != null && Utilities.IsValid(targetPlayer)) captain.ReportSighting(targetPlayer, pos);
    }

    // ---------- Chase ----------

    void DoChase(float dt)
    {
        if (!hasTarget)
        {
            StartPatrol(lastSeenPos);
            return;
        }

        // ScanTargets resets unseenTimer whenever a target is seen
        unseenTimer += dt;
        if (unseenTimer >= loseSightTime)
        {
            StartPatrol(lastSeenPos);
            return;
        }

        bool seenNow = visibleCount >= spotMinPoints;
        Vector3 flyPos = flyingGroup.position;
        float horiz = HorizDistance(flyPos, lastSeenPos);
        repathTimer -= dt;

        // Rush: it cannot (or may not) dive, so go straight for the player and detonate close to the body.
        bool rush = seenNow && !debugDisableDive && (rushTimer >= rushDelay || (rushIndoors && indoorNow));
        agent.speed = rush ? rushSpeed : chaseSpeed;

        if (rush)
        {
            agent.isStopped = false;
            if (repathTimer <= 0f)
            {
                repathTimer = repathInterval;
                // aim at the nearest navmesh point, so a player standing against a wall never sends the agent around it
                NavMeshHit nh;
                Vector3 goal = NavMesh.SamplePosition(targetFeet, out nh, 1.5f, -1) ? nh.position : targetFeet;
                agent.SetDestination(goal);
            }
            if (DistToBody(flyPos, targetFeet, targetBodyHeight) <= detonateRadius)
            {
                if (debugLog) Debug.Log("[Drone] rush: detonating next to the player");
                Explode(REASON_PLAYER);
                return;
            }
        }
        else if (seenNow && horiz < retreatDistance)
        {
            // too close to get a diving angle: back away
            agent.isStopped = false;
            if (repathTimer <= 0f)
            {
                repathTimer = repathInterval;
                Vector3 away = transform.position - lastSeenPos;
                away.y = 0f;
                if (away.sqrMagnitude < 0.01f) away = -flyingGroup.forward;
                away.y = 0f;
                agent.SetDestination(transform.position + away.normalized * retreatStep);
            }
        }
        else if (horiz < (seenNow ? chaseStopDistance : lostStopDistance))
        {
            // good distance (or at the last seen spot): hold and look
            agent.isStopped = true;
        }
        else
        {
            agent.isStopped = false;
            if (repathTimer <= 0f)
            {
                repathTimer = repathInterval;
                agent.SetDestination(lastSeenPos);
            }
        }

        if (seenNow)
        {
            reportTimer -= dt;
            if (reportTimer <= 0f)
            {
                reportTimer = reportInterval;
                ReportSighting(lastSeenPos);
            }

            // under a ceiling with rushIndoors the dive is skipped: the rush above handles it
            if (!debugDisableDive && cooldown <= 0f && !(rushIndoors && indoorNow))
            {
                TryStartDive();
                if (state == STATE_DIVE)
                {
                    rushTimer = 0f;
                    return;
                }
                rushTimer += dt;   // could not dive this frame; after rushDelay seconds the drone rushes instead
            }
        }
        else
        {
            rushTimer = 0f;
        }
    }

    // ---------- Target selection (owner only) ----------

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

    // Keeps the locked target's position fresh every frame.
    void RefreshTargetPosition()
    {
        if (Utilities.IsValid(targetPlayer))
        {
            targetFeet = targetPlayer.GetPosition();
            Vector3 head = targetPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
            targetBodyHeight = Mathf.Max(head.y - targetFeet.y, 0.3f);
            hasTarget = true;
        }
        else
        {
            targetPlayer = null;
            hasTarget = false;
        }
    }

    // Runs at visionInterval. Picks the target:
    //  - a visible current target stays the target (sticky)
    //  - otherwise the nearest visible eligible player
    //  - if nobody is visible the current target is kept (we chase the last seen spot),
    //    unless it died / left / stopped playing.
    void ScanTargets()
    {
        int n = CollectTargets();

        VRCPlayerApi chosen = null;
        int chosenCount = 0;
        bool chosenFeetVisible = false;
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
            chosenFeetVisible = scanFeetVisible;
            chosenFeet = feet;
            chosenDist = dist;
            chosenIsCurrent = isCurrent;
        }

        if (chosen != null)
        {
            targetPlayer = chosen;
            targetId = chosen.playerId;
            visibleCount = chosenCount;
            feetVisible = chosenFeetVisible;
            lastSeenPos = chosenFeet;
            unseenTimer = 0f;
            RefreshTargetPosition();
            return;
        }

        // nobody visible
        visibleCount = 0;
        feetVisible = false;
        if (Utilities.IsValid(targetPlayer) && !currentEligible)
        {
            targetPlayer = null;   // died, left, or stopped playing
            hasTarget = false;
        }
    }

    // ---------- Dive ----------

    float GetDiveEndHeight()
    {
        return explodeOnMissedDive ? 0f : diveEndHeight;
    }

    void TryStartDive()
    {
        Vector3 flyPos = flyingGroup.position;
        Vector3 feet = targetFeet;

        float h = flyPos.y - (feet.y + GetDiveEndHeight());
        if (h < 0.3f) { LogDiveBlock("too low, h=" + h); return; }   // not high enough to dive

        float d = HorizDistance(flyPos, feet);
        float angle = Mathf.Atan2(h, d) * Mathf.Rad2Deg;
        // under a ceiling the "dive" is a low, fast dash: shallower minimum angle, but only from close range
        float minAngle = indoorNow ? diveMinAngleIndoor : diveMinAngle;
        if (indoorNow && d > diveMaxDistanceIndoor)
        {
            LogDiveBlock("indoor: too far for a dash, " + d + " > " + diveMaxDistanceIndoor);
            return;
        }
        if (angle < minAngle || angle > diveMaxAngle)
        {
            LogDiveBlock("glide angle " + angle + " outside " + minAngle + "-" + diveMaxAngle + " (horizontal dist " + d + ")");
            return;
        }

        // the player must be roughly in front of the drone
        Vector3 toPlayer = feet - flyPos;
        toPlayer.y = 0f;
        Vector3 fwd = flyingGroup.forward;
        fwd.y = 0f;
        if (toPlayer.sqrMagnitude > 0.01f && fwd.sqrMagnitude > 0.01f)
        {
            float frontAngle = Vector3.Angle(fwd, toPlayer);
            if (frontAngle > diveFrontAngle)
            {
                LogDiveBlock("player not in front, " + frontAngle + " deg off facing");
                return;
            }
        }

        if (!feetVisible || visibleCount < minVisiblePoints)
        {
            LogDiveBlock("vision: feetVisible=" + feetVisible + " visiblePoints=" + visibleCount);
            return;
        }

        // physical check: is the straight line to the feet clear for the drone's body?
        if (!HasClearLine(flyPos, feet + Vector3.up * 0.1f, bodyRadius * 3f))
        {
            LogDiveBlock("line to feet blocked for the drone body");
            return;
        }

        BeginDive();
    }

    // Debug: why a dive did not start (throttled to twice a second)
    void LogDiveBlock(string why)
    {
        if (!debugDiveReasons) return;
        if (Time.time < nextReasonLog) return;
        nextReasonLog = Time.time + 0.5f;
        Debug.Log("[Drone] no dive: " + why);
    }

    void BeginDive()
    {
        Vector3 flyPos = flyingGroup.position;
        Vector3 feet = targetFeet;
        Vector3 flat = feet - flyPos;
        flat.y = 0f;
        float d = flat.magnitude;

        if (d > 0.01f)
        {
            diveDir = flat / d;
        }
        else
        {
            diveDir = flyingGroup.forward;
            diveDir.y = 0f;
            diveDir = diveDir.normalized;
        }

        diveCommit = feet;                           // locked: the player can dodge
        diveEndY = feet.y + GetDiveEndHeight();
        diveSlope = (flyPos.y - diveEndY) / Mathf.Max(d, 0.5f);
        diveTimer = diveMaxTime;
        divePassed = false;
        diveSteerTimer = 0f;   // NEW

        SetState(STATE_DIVE);   // also sends the dive data to the other clients

        // The overshoot point can lie behind a wall or off the navmesh. The agent would then path around to the nearest
        // reachable point (often outside the building) while the glide keeps dropping. Stop the destination at the navmesh edge.
        Vector3 dest = feet + diveDir * diveOvershoot;
        NavMeshHit edge;
        if (NavMesh.Raycast(transform.position, dest, out edge, -1)) dest = edge.position;
        agent.SetDestination(dest);
    }

    // Contact is NOT checked here: each client checks its own player (CheckLocalContact).
    void DoDive(float dt)
    {
        diveTimer -= dt;
        Vector3 flyPos = flyingGroup.position;

        Vector3 toCommit = diveCommit - flyPos;
        toCommit.y = 0f;
        if (Vector3.Dot(toCommit, diveDir) <= 0f) divePassed = true;

        bool finished = (divePassed && toCommit.magnitude > diveOvershoot * 0.8f) || diveTimer <= 0f;
        if (!finished)
        {
            SteerDive(dt);
            return;
        }

        // (with explodeOnMissedDive the ground contact is detected in LateUpdate; this is the safety net)
        if (explodeOnMissedDive) Explode(REASON_GROUND);
        else EndDive();
    }

    // Owner only. Swings the locked glide toward the player's CURRENT position, at most diveTurnRate degrees per second.
    // The remaining glide distance is kept, so the altitude formula in LateUpdate does not jump.
    void SteerDive(float dt)
    {
        if (diveTurnRate <= 0f || divePassed) return;
        if (!hasTarget || visibleCount < spotMinPoints) return;   // lost sight: keep the current heading

        diveSteerTimer += dt;
        if (diveSteerTimer < repathInterval) return;
        float step = diveSteerTimer;
        diveSteerTimer = 0f;

        Vector3 rootPos = transform.position;
        Vector3 toCommit = diveCommit - rootPos;
        toCommit.y = 0f;
        float remaining = Vector3.Dot(toCommit, diveDir);
        if (remaining < diveSteerStopDistance) return;            // too close: no last-moment swerve

        Vector3 toPlayer = targetFeet - rootPos;
        toPlayer.y = 0f;
        if (toPlayer.sqrMagnitude < 0.01f) return;

        float curYaw = Mathf.Atan2(diveDir.x, diveDir.z) * Mathf.Rad2Deg;
        float wantYaw = Mathf.Atan2(toPlayer.x, toPlayer.z) * Mathf.Rad2Deg;
        float maxStep = diveTurnRate * step;
        float delta = Mathf.Clamp(Mathf.DeltaAngle(curYaw, wantYaw), -maxStep, maxStep);
        if (Mathf.Abs(delta) < 0.1f) return;

        float newYaw = (curYaw + delta) * Mathf.Deg2Rad;
        diveDir = new Vector3(Mathf.Sin(newYaw), 0f, Mathf.Cos(newYaw));

        // same remaining distance, new direction (keeps the glide slope consistent)
        diveCommit = new Vector3(rootPos.x, diveCommit.y, rootPos.z) + diveDir * remaining;

        Vector3 dest = diveCommit + diveDir * diveOvershoot;
        NavMeshHit edge;
        if (NavMesh.Raycast(rootPos, dest, out edge, -1)) dest = edge.position;
        agent.SetDestination(dest);

        SyncNow();   // remote clients compute the glide altitude from diveCommit / diveDir
    }

    // Missed, recovering (only when explodeOnMissedDive is OFF)
    void EndDive()
    {
        cooldown = diveCooldown;
        altVel = 0f;
        SetState(STATE_CHASE);
        if (debugLog) Debug.Log("[Drone] dive missed, recovering");
    }

    // ---------- Hits (victim side, every client) ----------

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
            Debug.Log("[Drone] local player KILLED (" + why + ")");
        }
        else
        {
            Debug.Log("[Drone] (debug) local player would have been killed (" + why + ")");
        }
    }

    // During a dive, THIS client checks whether the drone (as it looks here) touches its own player.
    void CheckLocalContact()
    {
        if (state != STATE_DIVE)
        {
            localContactSent = false;
            return;
        }
        if (localContactSent) return;
        if (!CanLocalBeHit()) return;

        VRCPlayerApi lp = Networking.LocalPlayer;
        if (!Utilities.IsValid(lp)) return;

        Vector3 feet = lp.GetPosition();
        Vector3 head = lp.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
        float bodyHeight = Mathf.Max(head.y - feet.y, 0.3f);
        if (DistToBody(flyingGroup.position, feet, bodyHeight) >= hitRadius) return;

        localContactSent = true;
        KillLocalPlayer("dive contact");
        if (aiActive) Explode(REASON_PLAYER);
        else SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(NetHitPlayer));
    }

    // ---------- Exploded ----------

    // Owner only. Everyone else gets it through the synced explosion counter.
    void Explode(int reason)
    {
        if (!aiActive) return;
        if (state == STATE_EXPLODED || state == STATE_PARKED) return;

        Vector3 pos = flyingGroup.position;
        state = STATE_EXPLODED;
        explodeTimer = debugRespawnDelay;
        agent.isStopped = true;
        targetPlayer = null;
        hasTarget = false;

        if (debugLog) Debug.Log("[Drone] EXPLODED: " + ReasonName(reason));

        if (net != null)
        {
            net.syncExplosionSeq = net.syncExplosionSeq + 1;
            net.syncExplosionPos = pos;
            lastExplosionSeq = net.syncExplosionSeq;   // the owner never replays its own explosion
        }
        ExplosionLocal(pos);
        SyncNow();
        // TODO: notify the manager here (it will teleport the drone back with ResetToLaunch)
    }

    string ReasonName(int reason)
    {
        if (reason == REASON_SHOT) return "shot by player";
        if (reason == REASON_PLAYER) return "hit player";
        if (reason == REASON_GROUND) return "hit ground";
        return "unknown";
    }

    // Runs on EVERY client: effect, hide the drone, and kill the local player if inside the radius.
    void ExplosionLocal(Vector3 pos)
    {
        if (explosionEffect != null)
        {
            explosionEffect.transform.position = pos;
            explosionEffect.SetActive(false);
            explosionEffect.SetActive(true);
        }

        flyingGroup.gameObject.SetActive(false);

        VRCPlayerApi lp = Networking.LocalPlayer;
        if (!Utilities.IsValid(lp) || !CanLocalBeHit()) return;

        Vector3 feet = lp.GetPosition();
        Vector3 head = lp.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
        float bodyHeight = Mathf.Max(head.y - feet.y, 0.3f);
        float dist = DistToBody(pos, feet, bodyHeight);
        if (dist > explosionKillRadius) return;
        if (dist <= explosionCoreRadius || BlastReaches(pos, feet, bodyHeight)) KillLocalPlayer("explosion");
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
    void UpdateExploded(float dt)
    {
        if (captain != null || !debugAutoRespawn) return;
        explodeTimer -= dt;
        if (explodeTimer <= 0f) ResetToLaunch(launchPos);
    }

    // ================= vision =================

    // How many of feet / chest / head this drone can see. Also sets scanFeetVisible.
    int CountVisiblePoints(Vector3 feet, Vector3 head)
    {
        if (debugIgnoreVision)
        {
            scanFeetVisible = true;
            return 3;
        }

        Vector3 from = (eye != null) ? eye.position : flyingGroup.position;
        Vector3 footPt = feet + Vector3.up * 0.2f;
        Vector3 chestPt = Vector3.Lerp(feet, head, 0.5f);

        scanFeetVisible = CanSeePoint(from, footPt);
        int count = scanFeetVisible ? 1 : 0;
        if (CanSeePoint(from, chestPt)) count++;
        if (CanSeePoint(from, head)) count++;
        return count;
    }

    // Inside the view cone, in range, and no obstacle in between.
    bool CanSeePoint(Vector3 from, Vector3 point)
    {
        Transform e = (eye != null) ? eye : flyingGroup;
        Vector3 d = point - from;
        float dist = d.magnitude;
        if (dist < 0.01f) return true;
        if (dist > viewRange) return false;

        Vector3 dir = d / dist;
        if (Vector3.Angle(e.forward, dir) > viewAngle * 0.5f) return false;

        RaycastHit hit;
        // stop a little short so the ground right at the player's feet never counts as cover
        float castDist = Mathf.Max(dist - 0.15f, 0.01f);
        bool blocked = Physics.Raycast(from, dir, out hit, castDist, obstacleMask, QueryTriggerInteraction.Ignore);
        if (debugDrawRays) Debug.DrawLine(from, point, blocked ? Color.red : Color.green, visionInterval);
        return !blocked;
    }

    // ================= network sync =================

    // Owner -> everyone. Called on every state change and (rate limited) when altitude or yaw change.
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
        net.syncDiveCommit = diveCommit;
        net.syncDiveDir = diveDir;
        net.syncDiveEndY = diveEndY;
        net.syncDiveSlope = diveSlope;

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

    // Other clients receive the owner's state here (DroneNetSync calls this from its OnDeserialization).
    public void OnSyncReceived()
    {
        if (aiActive || net == null) return;

        state = net.syncState;
        travelDest = net.syncTravelDest;
        diveCommit = net.syncDiveCommit;
        diveDir = net.syncDiveDir;
        diveEndY = net.syncDiveEndY;
        diveSlope = net.syncDiveSlope;

        if (!gotFirstSync)
        {
            // late joiner: adopt the current picture, do not replay old explosions
            gotFirstSync = true;
            lastExplosionSeq = net.syncExplosionSeq;
            currentAlt = net.syncAlt;
            flyingGroup.rotation = Quaternion.Euler(0f, net.syncYaw, 0f);
        }
        else if (net.syncExplosionSeq != lastExplosionSeq)
        {
            lastExplosionSeq = net.syncExplosionSeq;
            ExplosionLocal(net.syncExplosionPos);
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
        float groundY = rootPos.y;               // the agent sits on the navmesh = ground height here

        Vector3 flatVel = (rootPos - lastRootPos) / Mathf.Max(dt, 0.0001f);
        flatVel.y = 0f;
        lastRootPos = rootPos;

        if (state == STATE_DIVE)
        {
            // Glide slope: altitude is a function of remaining horizontal distance,
            // so the drone arrives at the player's feet exactly when it arrives horizontally.
            Vector3 toCommit = diveCommit - rootPos;
            toCommit.y = 0f;
            float remaining = Mathf.Max(Vector3.Dot(toCommit, diveDir), 0f);
            float alt = diveEndY + diveSlope * remaining;
            currentAlt = Mathf.Min(alt, currentAlt);          // never climb during a dive
        }
        else
        {
            UpdateCeiling(dt, rootPos, flatVel);
            UpdateAltitude(GetTargetAltitude(groundY), dt);
        }

        // hard floor so a fast hill never swallows the drone
        float floorY = groundY + hardFloorClearance;
        if (currentAlt < floorY)
        {
            currentAlt = floorY;
            if (altVel < 0f) altVel = 0f;
        }

        flyingGroup.position = new Vector3(rootPos.x, currentAlt, rootPos.z);

        // facing (yaw only): look at the last seen spot while hunting, along the motion otherwise
        Vector3 look = flatVel;
        if (state == STATE_CHASE) look = lastSeenPos - rootPos;
        look.y = 0f;
        if (look.sqrMagnitude > 0.01f)
        {
            Quaternion want = Quaternion.LookRotation(look.normalized, Vector3.up);
            flyingGroup.rotation = Quaternion.RotateTowards(flyingGroup.rotation, want, turnSpeed * dt);
        }

        UpdateTilt(dt);

        // ground contact at the end of a committed dive
        if (state == STATE_DIVE && explodeOnMissedDive && currentAlt <= floorY + 0.02f)
        {
            Explode(REASON_GROUND);
            return;
        }

        MaybeSync(dt);
    }

    // Other clients: no AI. The root comes from Object Sync; altitude and yaw come from the synced values.
    // During a dive the altitude is computed from the (interpolated) root position, so it always matches it.
    void LateUpdateRemote(float dt)
    {
        if (state == STATE_EXPLODED || net == null) return;

        Vector3 rootPos = transform.position;

        if (state == STATE_DIVE)
        {
            Vector3 toCommit = diveCommit - rootPos;
            toCommit.y = 0f;
            float remaining = Mathf.Max(Vector3.Dot(toCommit, diveDir), 0f);
            float alt = diveEndY + diveSlope * remaining;
            currentAlt = Mathf.Min(alt, currentAlt);
        }
        else
        {
            float k = 1f - Mathf.Exp(-remoteAltSmoothing * dt);
            currentAlt = Mathf.Lerp(currentAlt, net.syncAlt, k);
        }

        float floorY = rootPos.y + hardFloorClearance;
        if (currentAlt < floorY) currentAlt = floorY;

        flyingGroup.position = new Vector3(rootPos.x, currentAlt, rootPos.z);

        Quaternion want = Quaternion.Euler(0f, net.syncYaw, 0f);
        flyingGroup.rotation = Quaternion.RotateTowards(flyingGroup.rotation, want, turnSpeed * 2f * dt);

        UpdateTilt(dt);
    }

    // ================= tilt animation (cosmetic) =================

    // Feeds the tilt Animator on EVERY client. The speed is measured from how the Flying Group actually moved,
    // so the owner and the other clients all see the same thing and nothing has to be synced.
    // VelForward / VelSide are the drone's velocity in its own directions, scaled to -1..+1 by tiltFullSpeed.
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

    // The altitude (world Y) to aim for. Normally the Travel / cruise height. If a ceiling, door lintel or bridge
    // deck lies ahead (or overhead), it descends to indoorHeight above the floor (or lower if the clearance is smaller).
    float GetTargetAltitude(float groundY)
    {
        if (state == STATE_PARKED)
        {
            SetIndoor(false);
            return groundY + minClearance;
        }

        float h = (state == STATE_TRAVEL) ? travelHeight : cruiseHeight;
        float normal = Mathf.Max(referenceGroundY + h, groundY + minClearance);

        float limit = ceilingLowestY - ceilingMargin;   // highest altitude that still clears everything ahead
        if (!useCeilingCheck || limit >= normal)
        {
            SetIndoor(false);
            return normal;
        }

        SetIndoor(true);
        float indoor = Mathf.Min(groundY + indoorHeight, limit);
        return Mathf.Max(indoor, groundY + indoorMinClearance);
    }

    void SetIndoor(bool value)
    {
        if (indoorNow == value) return;
        indoorNow = value;
        if (debugLog) Debug.Log(value ? "[Drone] under a ceiling: low mode" : "[Drone] clear of ceilings: normal height");
    }

    void ResetCeiling()
    {
        ceilingLowestY = 100000f;
        ceilingRaiseTimer = 0f;
        indoorNow = false;
        ceilingTimer = Random.Range(0f, ceilingCheckInterval);   // stagger the drones
    }

    // Owner only, a few times a second. Looks along the way ahead (not only where the drone is now):
    // from floor points every ceilingSampleSpacing meters it casts a thick sphere UP and remembers the lowest
    // ceiling it finds. The look-ahead distance covers the reaction time plus the time needed to descend,
    // so the drone is already low when it reaches a doorway, a bridge or a roof edge.
    void UpdateCeiling(float dt, Vector3 rootPos, Vector3 flatVel)
    {
        if (!useCeilingCheck)
        {
            ceilingLowestY = 100000f;
            return;
        }

        ceilingTimer -= dt;
        if (ceilingTimer > 0f) return;
        ceilingTimer = ceilingCheckInterval;

        // which way are we going
        Vector3 dir = flatVel;
        dir.y = 0f;
        float speed = dir.magnitude;
        if (speed > 0.5f)
        {
            dir = dir / speed;
        }
        else
        {
            dir = flyingGroup.forward;
            dir.y = 0f;
            dir = (dir.sqrMagnitude > 0.01f) ? dir.normalized : Vector3.forward;
            speed = 0f;
        }

        // how far ahead: reaction time plus the time it needs to come down from where it is now
        float aboveFloor = currentAlt - rootPos.y;
        float descendTime = Mathf.Max(aboveFloor - indoorHeight, 0f) / Mathf.Max(maxDescendSpeed, 0.1f);
        float lookDist = speed * (ceilingLookAheadTime + descendTime);
        lookDist = Mathf.Clamp(lookDist, ceilingMinLook, ceilingSampleSpacing * ceilingMaxSamples);

        // only ceilings lower than the height it would normally fly at matter
        float h = (state == STATE_TRAVEL) ? travelHeight : cruiseHeight;
        float normal = Mathf.Max(referenceGroundY + h, rootPos.y + minClearance);
        float startY = rootPos.y + ceilingStartHeight;
        float castLen = Mathf.Max(normal + ceilingMargin + ceilingSphereRadius - startY, 0.5f);

        float lowest = 100000f;
        int n = Mathf.Min(Mathf.CeilToInt(lookDist / ceilingSampleSpacing), ceilingMaxSamples);
        for (int i = 0; i <= n; i++)
        {
            Vector3 start = rootPos + dir * (i * ceilingSampleSpacing);
            start.y = startY;

            RaycastHit hit;
            if (Physics.SphereCast(start, ceilingSphereRadius, Vector3.up, out hit, castLen, obstacleMask, QueryTriggerInteraction.Ignore))
            {
                if (hit.distance > 0.01f)
                {
                    if (hit.point.y < lowest) lowest = hit.point.y;
                    if (debugDrawCeiling) Debug.DrawLine(start, hit.point, Color.cyan, ceilingCheckInterval);
                }
            }
            else if (debugDrawCeiling)
            {
                Debug.DrawLine(start, start + Vector3.up * castLen, Color.green, ceilingCheckInterval);
            }
        }

        // go down at once, but only come back up after the way has stayed clear for a moment (no flicker at roof edges)
        if (lowest < ceilingLowestY - 0.05f)
        {
            ceilingLowestY = lowest;
            ceilingRaiseTimer = 0f;
        }
        else if (lowest > ceilingLowestY + 0.05f)
        {
            ceilingRaiseTimer += ceilingCheckInterval;
            if (ceilingRaiseTimer >= ceilingRaiseDelay)
            {
                ceilingLowestY = lowest;
                ceilingRaiseTimer = 0f;
            }
        }
        else
        {
            ceilingRaiseTimer = 0f;
        }
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

    // stopShort: ignore the last bit of the line, so the dive's own landing spot (the ground) never counts as an obstacle
    bool HasClearLine(Vector3 from, Vector3 to, float stopShort)
    {
        Vector3 d = to - from;
        float dist = d.magnitude - stopShort;
        if (dist < 0.05f) return true;
        RaycastHit hit;
        return !Physics.SphereCast(from, bodyRadius, d.normalized, out hit, dist, obstacleMask, QueryTriggerInteraction.Ignore);
    }
}
