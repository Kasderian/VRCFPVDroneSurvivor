using TMPro;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// SpotterManager: ONE instance in the scene runs ALL (up to 5) fixed-wing Spotters.
///
/// MOVEMENT (every client, nothing synced)
///  The waypoints (children of pathParent) are smoothed into a closed loop at flightAltitude.
///  Spotter k flies at flightSpeed. Its place on the loop is a pure function of SERVER TIME:
///      loop fraction = (serverTime * speed / loopLength) + k / n        (n = active Spotters, see ActiveSpotterCount)
///  so Spotters are spread evenly (2 Spotters: 0% / 50%, 5 Spotters: 0 / 20 / 40 / 60 / 80%).
///  When the difficulty changes, Spotters jump to their new places (accepted on purpose).
///  ACTIVE SPOTTERS: difficulty 0 = none, otherwise max(difficulty, minSpotters). With minSpotters = 2:
///  difficulty 1 = 2, 2 = 2, 3 = 3, 4 = 4, 5 = 5.
///  Difficulty 0 or a Spotter on cooldown: its FlyingGroup is switched off (the root stays where it was).
///
/// BEING SHOT
///  SpotterHit (on the collider) -> ShotSpotter(k) on the shooter's client -> one network event to this object's
///  owner (kept equal to the Game Host) -> cooldownEnd[k] = serverTime + shotCooldown (the ONLY synced data).
///  A Spotter is "up" when k < ActiveSpotterCount() AND it is not in its cooldown (see CooldownOver: server time can be NEGATIVE).
///  It reappears by itself, on its slot.
///
/// VISION (Game Host's client only, ~4x a second)
///  Each up Spotter looks at ONE area (child of areasParent) for dwellSeconds, then takes the next one.
///  Areas are claimed (two Spotters never watch the same area), and picked by "longest unscanned" among the
///  areas that stay within maxViewRange for most of the coming dwell (the path is deterministic, so this is predictable).
///  For every playing + alive player inside the current area: 4 rays (head, chest, groin, shin) from the Spotter's eye.
///  A point with no hit is SEEN. At least minPointsSeen points must be seen for the player to count as visible.
///  One shared meter per player fills while any Spotter sees them (about 1 s at best, 2 s at worst, see tunables):
///  faster when closer, faster when moving fast, slower when only part of the body is seen. It drains slower when unseen.
///  Meter full + still visible: the sighting is reported to OpForCaptain every reportInterval seconds.
///
/// SETUP
///  Prefab (5 instances), child names must match EXACTLY:
///      Spotter (root, no script)
///        FlyingGroup
///          SpotterBody        mesh + NON-trigger collider (layer OpFor) + SpotterHit
///          EyeEmpty           ray origin, about 0.2 m below the body
///        ExplosionFx          particle system, explosion only (a sibling of FlyingGroup, NOT inside it)
///  Scene: this script (sync mode Manual is set below), pathParent (waypoint empties), areasParent (area empties).
///  Area radius = the empty's X scale in meters (scale below 1.5 = not set, defaultAreaRadius is used).
///  visionMask: tick ONLY the Default layer.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class SpotterManager : UdonSharpBehaviour
{
    private const int MaxSpotters = 5;
    private const int PointCount = 4;     // head, chest, groin, shin

    [Header("Scene references")]
    [SerializeField] private GameManager gameManager;
    [SerializeField] private OpForCaptain captain;
    [Tooltip("The SAME 6 PlayerSlot objects the PlayerRoster and OpForCaptain use.")]
    [SerializeField] private PlayerSlot[] slots;
    [Tooltip("Empty parent whose children are the waypoints (at least 3, in flying order).")]
    [SerializeField] private Transform pathParent;
    [Tooltip("Empty parent whose children are the search areas (X scale = radius in meters).")]
    [SerializeField] private Transform areasParent;
    [Tooltip("The roots of the 5 Spotter prefab instances. Order = slot order (index 0 is the first to appear).")]
    [SerializeField] private Transform[] spotterRoots;

    [Header("Spotter count")]
    [Tooltip("Fewest Spotters while the game runs (difficulty 1 and up). Difficulty 0 = none. Above this the count follows the difficulty.")]
    [SerializeField] private int minSpotters = 2;

    [Header("Flight")]
    [Tooltip("World Y of the flight height. Add your ground height here if the ground is not at Y = 0.")]
    [SerializeField] private float flightAltitude = 100f;
    [SerializeField] private float flightSpeed = 20f;
    [Tooltip("Distance between smoothed path samples (meters). Smaller = rounder, slightly more memory.")]
    [SerializeField] private float sampleSpacing = 8f;
    [Tooltip("A warning is logged at Start if the smoothed loop turns tighter than this (a fixed wing cannot).")]
    [SerializeField] private float minTurnRadius = 50f;
    [Tooltip("Heading is measured this far ahead on the loop (meters).")]
    [SerializeField] private float headingLookAhead = 8f;
    [SerializeField] private float maxBank = 35f;
    [Tooltip("Degrees of bank per degree/second of turn rate.")]
    [SerializeField] private float bankPerTurnRate = 1.5f;
    [SerializeField] private float bankSmoothing = 3f;

    [Header("Being shot")]
    [Tooltip("A shot Spotter is gone this long, then reappears on its slot.")]
    [SerializeField] private float shotCooldown = 45f;

    [Header("Vision")]
    [Tooltip("Tick ONLY the Default layer, so pickups, players and OpFor never block the rays.")]
    [SerializeField] private LayerMask visionMask;
    [SerializeField] private float maxViewRange = 500f;
    [Tooltip("Distance that counts as 'as close as it gets' (about the flight height).")]
    [SerializeField] private float nearDistance = 150f;
    [SerializeField] private float tickInterval = 0.25f;
    [SerializeField] private float dwellSeconds = 15f;
    [Tooltip("When the dwell is over, a player whose meter is filling may keep the Spotter here this much longer.")]
    [SerializeField] private float maxDwellExtension = 8f;
    [SerializeField] private float defaultAreaRadius = 35f;
    [Tooltip("An area needs to be within range in at least this many of 4 moments of the coming dwell.")]
    [SerializeField] private int minInRangeSamples = 2;
    [Tooltip("Area choice score: seconds of 'not scanned' per in-range sample.")]
    [SerializeField] private float inRangeWeight = 60f;

    [Header("Spotting meter")]
    [Tooltip("Time to spot a standing player at max range with all 4 points seen.")]
    [SerializeField] private float slowestSpotSeconds = 2f;
    [Tooltip("Time to spot a fast-moving player at the closest range with all 4 points seen.")]
    [SerializeField] private float fastestSpotSeconds = .5f;
    [Tooltip("Horizontal speed (m/s) that counts as motion. Your stamina script: walk 2, run 4.")]
    [SerializeField] private float detectMotionSpeed = 2f;
    [Tooltip("How much closeness counts compared with motion (0..1). The rest is motion.")]
    [SerializeField] private float distanceWeight = 0.5f;
    [Tooltip("Of the 4 body points, at least this many must be seen. Seen/4 also scales the fill speed.")]
    [SerializeField] private int minPointsSeen = 2;
    [SerializeField] private float meterDrainPerSecond = 0.5f;
    [SerializeField] private float reportInterval = 1f;
    [Tooltip("Ray points as a fraction of the player's height (head = the tracked head position).")]
    [SerializeField] private float chestFraction = 0.75f;
    [SerializeField] private float groinFraction = 0.5f;
    [SerializeField] private float shinFraction = 0.2f;
    [Tooltip("Rays stop this far short of the point, so the ground or a wall right at the body never counts as cover.")]
    [SerializeField] private float rayStopShort = 0.15f;

    [Header("Debug")]
    [SerializeField] private bool debugLog;
    [SerializeField] private bool debugDrawRays;
    [Tooltip("Optional world-space TextMeshPro text. Builds have no inspector, so this shows why Spotters do (not) appear. Leave empty for release.")]
    [SerializeField] private TextMeshProUGUI debugText;

    // ---- synced: the ONLY network data ----
    [UdonSynced] private double[] cooldownEnd = new double[MaxSpotters];

    // ---- local copies / bookkeeping ----
    private double[] seenCooldownEnd = new double[MaxSpotters];
    private bool gotFirstSync;
    private bool ready;
    private float followTimer;
    private bool startFailed;
    private string startStatus = "Start has not run (script halted or object inactive)";
    private float debugTimer;

    // ---- loop ----
    private Vector3[] pts;        // smoothed loop points, pts[segCount] == pts[0]
    private float[] cum;          // distance along the loop at each point
    private int segCount;
    private float loopLength;

    // ---- spotters ----
    private int spotterCount;
    private Transform[] groups;
    private Transform[] eyes;
    private Transform[] fxs;
    private SpotterHit[] hits;
    private bool[] shown;
    private float[] bank;
    private float[] prevYaw;
    private bool[] hasPrevYaw;
    private float[] hitLock;
    private string[] hitEvents;

    // ---- areas ----
    private int areaCount;
    private Vector3[] areaPos;
    private float[] areaRadius;
    private float[] areaLastScan;
    private int[] areaClaim;

    // ---- vision, per spotter ----
    private int[] curArea;
    private float[] dwellTimer;

    // ---- vision, per player slot ----
    private VRCPlayerApi[] playerApis;
    private bool[] playerOk;
    private Vector3[] playerPos;
    private Vector3[] lastPos;
    private float[] speedSmooth;
    private int[] trackedId;
    private float[] meter;
    private float[] nextReport;
    private bool[] sawNow;

    // ---- vision, tick ----
    private float tickAccum;
    private bool wasHost;
    private int lastDifficulty = -1;

    void Start()
    {
        if (gameManager == null) { Fail("GameManager not assigned"); return; }
        if (captain == null) { Fail("OpForCaptain not assigned"); return; }
        if (slots == null || slots.Length == 0) { Fail("no PlayerSlots assigned"); return; }
        if (spotterRoots == null || spotterRoots.Length == 0) { Fail("no Spotter roots assigned"); return; }

        BuildPath();
        if (loopLength < 10f) { Fail("loop is shorter than 10 m"); return; }

        BuildAreas();
        if (!SetupSpotters()) return;

        int pc = slots.Length;
        playerApis = new VRCPlayerApi[pc];
        playerOk = new bool[pc];
        playerPos = new Vector3[pc];
        lastPos = new Vector3[pc];
        speedSmooth = new float[pc];
        trackedId = new int[pc];
        meter = new float[pc];
        nextReport = new float[pc];
        sawNow = new bool[pc];
        for (int i = 0; i < pc; i++) trackedId[i] = -1;

        hitEvents = new string[] { nameof(Hit0), nameof(Hit1), nameof(Hit2), nameof(Hit3), nameof(Hit4) };

        ready = true;
        startStatus = "OK";
    }

    // Start cannot continue: log it AND remember it for the debug text (a build has no console).
    void Fail(string msg)
    {
        if (!startFailed) startStatus = "START FAILED: " + msg;
        startFailed = true;
        Debug.LogWarning("[Spotter] " + msg);
    }

    // ================= setup =================

    // Smooths the waypoints (centripetal Catmull-Rom, passes through every waypoint, no loops or cusps)
    // into a closed polyline with a distance table, so a place on the loop can be looked up by distance.
    void BuildPath()
    {
        if (pathParent == null) { Fail("path parent not assigned"); return; }
        int wp = pathParent.childCount;
        if (wp < 3) { Fail("the path needs at least 3 waypoints, found " + wp.ToString()); return; }

        Vector3[] w = new Vector3[wp];
        for (int i = 0; i < wp; i++)
        {
            Vector3 p = pathParent.GetChild(i).position;
            p.y = flightAltitude;
            w[i] = p;
        }

        int[] samples = new int[wp];
        int total = 0;
        for (int i = 0; i < wp; i++)
        {
            float len = Vector3.Distance(w[i], w[(i + 1) % wp]);
            int s = Mathf.Clamp(Mathf.CeilToInt(len / Mathf.Max(sampleSpacing, 1f)), 3, 256);
            samples[i] = s;
            total += s;
        }

        pts = new Vector3[total + 1];
        int n = 0;
        for (int i = 0; i < wp; i++)
        {
            Vector3 p0 = w[(i + wp - 1) % wp];
            Vector3 p1 = w[i];
            Vector3 p2 = w[(i + 1) % wp];
            Vector3 p3 = w[(i + 2) % wp];
            for (int s = 0; s < samples[i]; s++)
            {
                pts[n] = CatmullRom(p0, p1, p2, p3, (float)s / (float)samples[i]);
                n++;
            }
        }
        pts[total] = pts[0];
        segCount = total;

        cum = new float[total + 1];
        float acc = 0f;
        cum[0] = 0f;
        for (int i = 0; i < total; i++)
        {
            acc += Vector3.Distance(pts[i], pts[i + 1]);
            cum[i + 1] = acc;
        }
        loopLength = acc;

        CheckTurnRadius(w, wp);
        Debug.Log("[Spotter] loop: " + wp + " waypoints, " + loopLength.ToString("0") + " m, one lap = "
            + (loopLength / Mathf.Max(flightSpeed, 0.1f)).ToString("0") + " s");
    }

    // Centripetal Catmull-Rom between p1 and p2 (u = 0..1).
    Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u)
    {
        float t0 = 0f;
        float t1 = t0 + KnotStep(p0, p1);
        float t2 = t1 + KnotStep(p1, p2);
        float t3 = t2 + KnotStep(p2, p3);
        float t = Mathf.Lerp(t1, t2, u);

        Vector3 a1 = p0 * ((t1 - t) / (t1 - t0)) + p1 * ((t - t0) / (t1 - t0));
        Vector3 a2 = p1 * ((t2 - t) / (t2 - t1)) + p2 * ((t - t1) / (t2 - t1));
        Vector3 a3 = p2 * ((t3 - t) / (t3 - t2)) + p3 * ((t - t2) / (t3 - t2));
        Vector3 b1 = a1 * ((t2 - t) / (t2 - t0)) + a2 * ((t - t0) / (t2 - t0));
        Vector3 b2 = a2 * ((t3 - t) / (t3 - t1)) + a3 * ((t - t1) / (t3 - t1));
        return b1 * ((t2 - t) / (t2 - t1)) + b2 * ((t - t1) / (t2 - t1));
    }

    float KnotStep(Vector3 a, Vector3 b)
    {
        return Mathf.Sqrt(Mathf.Max(Vector3.Distance(a, b), 0.01f));
    }

    // Logs ONE warning with the tightest turn of the loop if it is tighter than minTurnRadius.
    void CheckTurnRadius(Vector3[] w, int wp)
    {
        float worst = 100000f;
        Vector3 worstPos = pts[0];
        for (int i = 0; i < segCount; i++)
        {
            Vector3 a = pts[(i + segCount - 1) % segCount];
            Vector3 b = pts[i];
            Vector3 c = pts[i + 1];
            Vector3 ab = b - a;
            Vector3 bc = c - b;
            Vector3 ac = c - a;
            float cross = Mathf.Abs(ab.x * bc.z - ab.z * bc.x);
            if (cross < 0.0001f) continue;
            float r = (ab.magnitude * bc.magnitude * ac.magnitude) / (2f * cross);
            if (r < worst) { worst = r; worstPos = b; }
        }

        if (worst >= minTurnRadius) return;

        int nearest = 0;
        float nearestD = 100000f;
        for (int i = 0; i < wp; i++)
        {
            float d = HorizDistance(w[i], worstPos);
            if (d < nearestD) { nearestD = d; nearest = i; }
        }
        Debug.LogWarning("[Spotter] tightest turn is only " + worst.ToString("0") + " m radius (wanted at least "
            + minTurnRadius.ToString("0") + ") near waypoint #" + nearest + " (" + pathParent.GetChild(nearest).name
            + "). Move waypoints apart or smooth the corner.");
    }

    void BuildAreas()
    {
        areaCount = (areasParent != null) ? areasParent.childCount : 0;
        if (areaCount == 0) Debug.LogWarning("[Spotter] no areas: Spotters fly but never spot anyone");

        areaPos = new Vector3[areaCount];
        areaRadius = new float[areaCount];
        areaLastScan = new float[areaCount];
        areaClaim = new int[areaCount];
        for (int i = 0; i < areaCount; i++)
        {
            Transform a = areasParent.GetChild(i);
            areaPos[i] = a.position;
            float r = a.lossyScale.x;
            if (r < 1.5f) r = defaultAreaRadius;
            areaRadius[i] = r;
            areaLastScan[i] = -1000f;
            areaClaim[i] = -1;
        }
    }

    // Finds the child objects by name, gives each SpotterHit its manager + index, hides everything.
    bool SetupSpotters()
    {
        spotterCount = Mathf.Min(spotterRoots.Length, MaxSpotters);
        if (spotterRoots.Length > MaxSpotters) Debug.LogWarning("[Spotter] more than " + MaxSpotters + " roots assigned, extras are ignored");

        groups = new Transform[spotterCount];
        eyes = new Transform[spotterCount];
        fxs = new Transform[spotterCount];
        hits = new SpotterHit[spotterCount];
        shown = new bool[spotterCount];
        bank = new float[spotterCount];
        prevYaw = new float[spotterCount];
        hasPrevYaw = new bool[spotterCount];
        hitLock = new float[spotterCount];
        curArea = new int[spotterCount];
        dwellTimer = new float[spotterCount];

        for (int k = 0; k < spotterCount; k++)
        {
            Transform root = spotterRoots[k];
            if (root == null) { Fail("Spotter root " + k + " is empty"); return false; }

            groups[k] = root.Find("FlyingGroup");
            eyes[k] = root.Find("FlyingGroup/EyeEmpty");
            fxs[k] = root.Find("ExplosionFx");
            Transform body = root.Find("FlyingGroup/SpotterBody");

            if (groups[k] == null || eyes[k] == null || fxs[k] == null || body == null)
            {
                Fail(root.name + ": expected children FlyingGroup, FlyingGroup/EyeEmpty, FlyingGroup/SpotterBody and ExplosionFx");
                return false;
            }

            hits[k] = body.GetComponent<SpotterHit>();
            if (hits[k] == null)
            {
                Debug.LogWarning("[Spotter] " + root.name + ": no SpotterHit on SpotterBody, this Spotter cannot be shot");
            }
            else
            {
                hits[k].manager = this;
                hits[k].index = k;
            }

            groups[k].gameObject.SetActive(false);
            fxs[k].gameObject.SetActive(false);
            shown[k] = false;
            curArea[k] = -1;
        }
        return true;
    }

    // ================= per frame: movement (every client) =================

    void Update()
    {
        float dt = Time.deltaTime;
        UpdateDebugText(dt);   // also runs when Start failed, to show why

        if (!ready) return;

        followTimer -= dt;
        if (followTimer <= 0f)
        {
            followTimer = 1f;
            FollowGameHost();
        }

        int diff = ActiveSpotterCount();   // number of Spotters flying (not the raw difficulty)
        double now = Networking.GetServerTimeInSeconds();

        for (int k = 0; k < spotterCount; k++)
        {
            bool up = k < diff && CooldownOver(k, now);
            if (up)
            {
                MoveSpotter(k, diff, now, dt);
                if (!shown[k]) SetShown(k, true);   // after the first placement, so it never flashes at an old spot
            }
            else if (shown[k])
            {
                SetShown(k, false);
            }
        }

        tickAccum += dt;
        if (tickAccum >= tickInterval)
        {
            float step = tickAccum;
            tickAccum = 0f;
            Tick(step, diff, now);
        }
    }

    // ================= debug readout (optional) =================

    // Shows on every client what the spawn logic sees. If this text stops changing, the script was halted by an Udon exception.
    void UpdateDebugText(float dt)
    {
        if (debugText == null) return;
        debugTimer -= dt;
        if (debugTimer > 0f) return;
        debugTimer = 0.5f;

        if (!ready)
        {
            debugText.text = "SpotterManager NOT READY\n" + startStatus;
            return;
        }

        int diff = ActiveSpotterCount();
        double now = Networking.GetServerTimeInSeconds();
        VRCPlayerApi lp = Networking.LocalPlayer;

        string t = "SpotterManager READY\n";
        t += "difficulty " + gameManager.GetDifficulty().ToString() + "  spotters " + diff.ToString() + "  gameHost " + (gameManager.IsGameHost() ? "YES" : "no") + "  owner " + (Networking.IsOwner(gameObject) ? "YES" : "no") + "\n";
        t += "serverTime " + ((float)now).ToString("0.0") + "  loop " + loopLength.ToString("0") + " m  lap " + (loopLength / Mathf.Max(flightSpeed, 0.1f)).ToString("0") + " s  areas " + areaCount.ToString() + "\n";

        for (int k = 0; k < spotterCount; k++)
        {
            bool up = k < diff && CooldownOver(k, now);
            Vector3 p = spotterRoots[k].position;
            t += "S" + k.ToString() + (up ? " UP" : " off")
                + "  shown " + (shown[k] ? "Y" : "n")
                + "  active " + (groups[k].gameObject.activeInHierarchy ? "Y" : "n")
                + "  pos " + p.x.ToString("0") + "," + p.y.ToString("0") + "," + p.z.ToString("0")
                + "  cooldown " + (CooldownOver(k, now) ? "-" : ((float)(cooldownEnd[k] - now)).ToString("0"))
                + "  area " + curArea[k].ToString();
            if (Utilities.IsValid(lp)) t += "  dist " + Vector3.Distance(lp.GetPosition(), p).ToString("0");
            t += "\n";
        }

        // player lines are only filled on the Game Host's client
        for (int i = 0; i < slots.Length; i++)
        {
            t += "P" + i.ToString() + (playerOk[i] ? " ok " : " -- ") + meter[i].ToString("0.00") + (sawNow[i] ? " SEEN" : "") + "\n";
        }

        debugText.text = t;
    }

    void SetShown(int k, bool value)
    {
        shown[k] = value;
        groups[k].gameObject.SetActive(value);
        hasPrevYaw[k] = false;
        bank[k] = 0f;
        if (!value) groups[k].localRotation = Quaternion.identity;
    }

    void MoveSpotter(int k, int n, double now, float dt)
    {
        float d = SpotterDistance(k, n, now);
        Vector3 p = PathPoint(d);

        float d2 = d + headingLookAhead;
        if (d2 >= loopLength) d2 -= loopLength;
        Vector3 dir = PathPoint(d2) - p;
        dir.y = 0f;

        float yaw = prevYaw[k];
        if (dir.sqrMagnitude > 0.0001f) yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;

        // bank into the turn (cosmetic): turning right (yaw increasing) = right wing down = negative roll about Z
        float bankTarget = 0f;
        if (hasPrevYaw[k] && dt > 0.0001f)
        {
            float dy = Mathf.DeltaAngle(prevYaw[k], yaw);
            if (Mathf.Abs(dy) < 45f) bankTarget = Mathf.Clamp(dy / dt * bankPerTurnRate, -maxBank, maxBank);
        }
        float kSmooth = 1f - Mathf.Exp(-bankSmoothing * dt);
        bank[k] = Mathf.Lerp(bank[k], bankTarget, kSmooth);

        spotterRoots[k].SetPositionAndRotation(p, Quaternion.Euler(0f, yaw, 0f));
        groups[k].localRotation = Quaternion.Euler(0f, 0f, -bank[k]);

        prevYaw[k] = yaw;
        hasPrevYaw[k] = true;
    }

    // ================= the loop =================

    // Fraction of a lap (0..1) at the given server time. The lap count is subtracted in double precision.
    float LoopFraction(double time)
    {
        double x = time * (double)flightSpeed / (double)loopLength;
        int whole = (int)x;
        double frac = x - (double)whole;
        if (frac < 0.0) frac += 1.0;     // (right whether the cast rounds or truncates)
        if (frac >= 1.0) frac -= 1.0;
        return (float)frac;
    }

    // Distance along the loop of Spotter k when n Spotters are active.
    float SpotterDistance(int k, int n, double time)
    {
        float f = LoopFraction(time) + (float)k / (float)Mathf.Max(n, 1);
        if (f >= 1f) f -= 1f;
        return f * loopLength;
    }

    Vector3 PathPoint(float d)
    {
        int j = FindSegment(d);
        float segLen = cum[j + 1] - cum[j];
        float t = (segLen > 0.0001f) ? (d - cum[j]) / segLen : 0f;
        return Vector3.Lerp(pts[j], pts[j + 1], t);
    }

    // Largest j with cum[j] <= d (binary search).
    int FindSegment(float d)
    {
        int lo = 0;
        int hi = segCount - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (cum[mid] <= d) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    // ================= being shot =================

    // Called by SpotterHit on the SHOOTER's client.
    public void ShotSpotter(int k)
    {
        if (!ready || !IsSpotterUp(k)) return;
        if (Time.time < hitLock[k]) return;          // one event per shot-down, however many bullets land
        hitLock[k] = Time.time + 1f;

        if (Networking.IsOwner(gameObject)) ApplyHit(k);
        else SendCustomNetworkEvent(NetworkEventTarget.Owner, hitEvents[k]);
    }

    // Udon events cannot carry an argument, so there is one tiny receiver per Spotter (they run on the owner).
    public void Hit0() { NetHit(0); }
    public void Hit1() { NetHit(1); }
    public void Hit2() { NetHit(2); }
    public void Hit3() { NetHit(3); }
    public void Hit4() { NetHit(4); }

    void NetHit(int k)
    {
        if (!ready || !Networking.IsOwner(gameObject)) return;
        ApplyHit(k);
    }

    // Owner only.
    void ApplyHit(int k)
    {
        if (!IsSpotterUp(k)) return;                 // already down (two shooters at the same moment)

        cooldownEnd[k] = Networking.GetServerTimeInSeconds() + (double)shotCooldown;
        gotFirstSync = true;
        RequestSerialization();
        ProcessCooldownChanges();                    // the owner does not get OnDeserialization
        if (debugLog) Debug.Log("[Spotter] Spotter " + k + " shot down");
    }

    // True when Spotter k is NOT in its shot-down cooldown.
    // Do NOT compare server time with 0 or with an old absolute value: VRChat's server clock is a 32-bit millisecond counter that
    // wraps, so it is NEGATIVE for about 24 days out of every 50 (seen in a build: -2077255 s). cooldownEnd starts at 0, so a plain
    // "now >= cooldownEnd" is false for that whole time and every Spotter stays down.
    // A real cooldown is never further ahead than shotCooldown, so anything beyond that (plus a margin for clock differences)
    // means "never shot" (0) or "the clock wrapped since it was set": the cooldown is over.
    bool CooldownOver(int k, double now)
    {
        double remaining = cooldownEnd[k] - now;
        return remaining <= 0.0 || remaining > (double)shotCooldown + 60.0;
    }

    bool IsSpotterUp(int k)
    {
        if (k < 0 || k >= spotterCount) return false;
        return k < ActiveSpotterCount() && CooldownOver(k, Networking.GetServerTimeInSeconds());
    }

    // How many Spotters fly: 0 while the game is stopped (difficulty 0), otherwise at least minSpotters.
    // Never more than the Spotters that exist (spotterCount). Used by movement, vision, hits and the debug text,
    // so they all agree. The raw difficulty is not used for Spotters anywhere else.
    int ActiveSpotterCount()
    {
        int difficulty = gameManager.GetDifficulty();
        if (difficulty <= 0) return 0;
        return Mathf.Clamp(Mathf.Max(difficulty, minSpotters), 0, spotterCount);
    }

    public override void OnDeserialization()
    {
        if (!gotFirstSync)
        {
            // late joiner: adopt the current picture, do not replay old explosions
            gotFirstSync = true;
            for (int k = 0; k < MaxSpotters; k++) seenCooldownEnd[k] = cooldownEnd[k];
            return;
        }
        if (!ready) return;
        ProcessCooldownChanges();
    }

    // A changed cooldown that lies in the future while the Spotter is visible = it was just shot down: explode.
    void ProcessCooldownChanges()
    {
        double now = Networking.GetServerTimeInSeconds();
        for (int k = 0; k < spotterCount; k++)
        {
            if (cooldownEnd[k] == seenCooldownEnd[k]) continue;
            seenCooldownEnd[k] = cooldownEnd[k];
            if (!CooldownOver(k, now) && shown[k]) PlayExplosion(k);
        }
    }

    void PlayExplosion(int k)
    {
        fxs[k].position = groups[k].position;
        fxs[k].gameObject.SetActive(false);
        fxs[k].gameObject.SetActive(true);
    }

    // The Game Host's client owns this object, so shot-down events always arrive where the cooldown is written.
    void FollowGameHost()
    {
        if (!gameManager.IsGameHost()) return;
        if (Networking.IsOwner(gameObject)) return;
        Networking.SetOwner(Networking.LocalPlayer, gameObject);
    }

    // ================= vision (Game Host's client only) =================

    void Tick(float step, int diff, double now)
    {
        if (!gameManager.IsGameHost())
        {
            if (wasHost) { ResetVision(); wasHost = false; }
            return;
        }
        if (!wasHost)
        {
            wasHost = true;
            lastDifficulty = -1;
            ResetVision();
        }

        if (diff != lastDifficulty)
        {
            // Spotters changed places: the areas picked from the old plan are stale
            lastDifficulty = diff;
            ReleaseAllAreas();
        }
        if (diff == 0)
        {
            ClearMeters();
            return;
        }

        // 1) who can be spotted, and how fast they move
        int pc = slots.Length;
        for (int i = 0; i < pc; i++)
        {
            sawNow[i] = false;
            VRCPlayerApi p = slots[i].GetOccupant();
            if (p == null || !slots[i].IsAlive())
            {
                playerOk[i] = false;
                playerApis[i] = null;
                meter[i] = 0f;
                trackedId[i] = -1;
                continue;
            }

            Vector3 pos = p.GetPosition();
            if (trackedId[i] != p.playerId)
            {
                // new player in this slot: start clean
                trackedId[i] = p.playerId;
                meter[i] = 0f;
                speedSmooth[i] = 0f;
                nextReport[i] = 0f;
                lastPos[i] = pos;
            }

            float dx = pos.x - lastPos[i].x;
            float dz = pos.z - lastPos[i].z;
            float raw = Mathf.Sqrt(dx * dx + dz * dz) / Mathf.Max(step, 0.01f);
            if (raw > 15f) raw = 0f;                 // teleport (cemetery, respawn): not running
            speedSmooth[i] = Mathf.Lerp(speedSmooth[i], raw, 0.5f);

            lastPos[i] = pos;
            playerPos[i] = pos;
            playerApis[i] = p;
            playerOk[i] = true;
        }

        // 2) every Spotter that is up looks at its area
        for (int k = 0; k < spotterCount; k++)
        {
            bool up = k < diff && CooldownOver(k, now);
            if (!up)
            {
                if (curArea[k] >= 0) ReleaseArea(k);
                continue;
            }

            UpdateArea(k, step, now, diff);
            if (curArea[k] >= 0) ScanArea(k, step);
        }

        // 3) meters: drain when nobody saw the player, report when full and still visible
        for (int i = 0; i < pc; i++)
        {
            if (!playerOk[i]) continue;

            if (!sawNow[i])
            {
                meter[i] = Mathf.Max(0f, meter[i] - meterDrainPerSecond * step);
                continue;
            }

            if (meter[i] >= 1f)
            {
                meter[i] = 1f;
                if (Time.time >= nextReport[i])
                {
                    nextReport[i] = Time.time + reportInterval;
                    captain.ReportSighting(playerApis[i], playerPos[i]);
                    if (debugLog) Debug.Log("[Spotter] player in slot " + i + " spotted at " + playerPos[i]);
                }
            }
        }
    }

    // Keeps the Spotter's current area, or takes the next one when the dwell is over.
    void UpdateArea(int k, float step, double now, int diff)
    {
        if (curArea[k] >= 0)
        {
            areaLastScan[curArea[k]] = Time.time;
            dwellTimer[k] -= step;
            if (dwellTimer[k] > 0f) return;
            // dwell over: let a spotting that is under way finish first (bounded)
            if (dwellTimer[k] > -maxDwellExtension && AreaHasFillingPlayer(curArea[k])) return;
        }
        else
        {
            dwellTimer[k] -= step;                   // idle: look for an area again when this runs out
            if (dwellTimer[k] > 0f) return;
        }

        PickArea(k, now, diff);
    }

    // Next area: not claimed by another Spotter, not the one it just left, in range for most of the coming dwell,
    // and the one that has gone unscanned the longest (in-range samples add a bonus).
    void PickArea(int k, double now, int diff)
    {
        bool fresh = curArea[k] < 0;
        int old = curArea[k];
        ReleaseArea(k);

        int best = -1;
        float bestScore = -1f;
        for (int a = 0; a < areaCount; a++)
        {
            if (areaClaim[a] >= 0) continue;
            if (a == old && areaCount > 1) continue;

            int inRange = PredictedInRange(k, a, now, diff);
            if (inRange < minInRangeSamples) continue;

            float stale = Mathf.Min(Time.time - areaLastScan[a], 300f);
            float score = stale + inRange * inRangeWeight + Random.Range(0f, 5f);
            if (score > bestScore)
            {
                bestScore = score;
                best = a;
            }
        }

        if (best < 0)
        {
            dwellTimer[k] = 1f;                      // nothing suitable right now, try again in a second
            return;
        }

        curArea[k] = best;
        areaClaim[best] = k;
        // a Spotter that just (re)appeared gets a shorter first dwell, so the Spotters do not all switch at the same moment
        dwellTimer[k] = fresh ? dwellSeconds * Random.Range(0.4f, 1f) : dwellSeconds;
        if (debugLog) Debug.Log("[Spotter] Spotter " + k + " watches area " + best);
    }

    // At how many of 4 moments of the coming dwell (start, 1/3, 2/3, end) is the area within view range of this Spotter?
    // The loop is a function of time, so the future position is known exactly.
    int PredictedInRange(int k, int a, double now, int diff)
    {
        int count = 0;
        for (int s = 0; s < 4; s++)
        {
            double t = now + (double)(dwellSeconds * (float)s / 3f);
            Vector3 plane = PathPoint(SpotterDistance(k, diff, t));
            if (Vector3.Distance(plane, areaPos[a]) <= maxViewRange) count++;
        }
        return count;
    }

    bool AreaHasFillingPlayer(int a)
    {
        Vector3 c = areaPos[a];
        float r2 = areaRadius[a] * areaRadius[a];
        for (int i = 0; i < slots.Length; i++)
        {
            if (!playerOk[i] || meter[i] <= 0.05f) continue;
            float dx = playerPos[i].x - c.x;
            float dz = playerPos[i].z - c.z;
            if (dx * dx + dz * dz <= r2) return true;
        }
        return false;
    }

    // Checks every playing + alive player inside the Spotter's area and fills their meter.
    void ScanArea(int k, float step)
    {
        int a = curArea[k];
        Vector3 c = areaPos[a];
        float r2 = areaRadius[a] * areaRadius[a];
        Vector3 eye = eyes[k].position;

        for (int i = 0; i < slots.Length; i++)
        {
            if (!playerOk[i]) continue;

            Vector3 pos = playerPos[i];
            float dx = pos.x - c.x;
            float dz = pos.z - c.z;
            if (dx * dx + dz * dz > r2) continue;                    // not in this area

            float dist = Vector3.Distance(eye, pos);
            if (dist > maxViewRange) continue;                       // too far to see

            Vector3 head = playerApis[i].GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
            int seen = CountSeenPoints(eye, pos, head);
            if (seen < minPointsSeen) continue;

            // closer and faster = quicker. 'closeness' runs from nearDistance (1) to maxViewRange (0).
            float closeness = 1f - Mathf.InverseLerp(nearDistance, maxViewRange, dist);
            float motion = Mathf.Clamp01(speedSmooth[i] / Mathf.Max(detectMotionSpeed, 0.1f));
            float f = Mathf.Clamp01(closeness * distanceWeight + motion * (1f - distanceWeight));
            float rate = Mathf.Lerp(1f / Mathf.Max(slowestSpotSeconds, 0.1f), 1f / Mathf.Max(fastestSpotSeconds, 0.1f), f);
            rate = rate * ((float)seen / (float)PointCount);

            meter[i] = Mathf.Min(1f, meter[i] + rate * step);
            sawNow[i] = true;
        }
    }

    // How many of head / chest / groin / shin have a clear line from the eye. Stops early when the minimum is out of reach.
    int CountSeenPoints(Vector3 eye, Vector3 feet, Vector3 head)
    {
        float h = Mathf.Max(head.y - feet.y, 0.3f);
        int seen = 0;
        for (int i = 0; i < PointCount; i++)
        {
            Vector3 pt;
            if (i == 0) pt = head;
            else if (i == 1) pt = feet + Vector3.up * (h * chestFraction);
            else if (i == 2) pt = feet + Vector3.up * (h * groinFraction);
            else pt = feet + Vector3.up * (h * shinFraction);

            if (CanSeePoint(eye, pt)) seen++;
            if (seen + (PointCount - 1 - i) < minPointsSeen) return seen;
        }
        return seen;
    }

    // No hit between the eye and (just short of) the point = seen. Only the layers in visionMask can block.
    bool CanSeePoint(Vector3 from, Vector3 point)
    {
        Vector3 d = point - from;
        float dist = d.magnitude;
        if (dist < 0.01f) return true;

        RaycastHit hit;
        float castDist = Mathf.Max(dist - rayStopShort, 0.01f);
        bool blocked = Physics.Raycast(from, d / dist, out hit, castDist, visionMask, QueryTriggerInteraction.Ignore);
        if (debugDrawRays) Debug.DrawLine(from, point, blocked ? Color.red : Color.green, tickInterval);
        return !blocked;
    }

    // ================= vision bookkeeping =================

    void ReleaseArea(int k)
    {
        int a = curArea[k];
        if (a >= 0 && areaClaim[a] == k) areaClaim[a] = -1;
        curArea[k] = -1;
    }

    void ReleaseAllAreas()
    {
        for (int k = 0; k < spotterCount; k++)
        {
            ReleaseArea(k);
            dwellTimer[k] = 0f;
        }
    }

    void ClearMeters()
    {
        for (int i = 0; i < meter.Length; i++)
        {
            meter[i] = 0f;
            trackedId[i] = -1;
            nextReport[i] = 0f;
        }
    }

    void ResetVision()
    {
        ReleaseAllAreas();
        ClearMeters();
    }

    // ================= helpers =================

    float HorizDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
