using UdonSharp;
using UnityEngine;
using UnityEngine.AI;
using VRC.SDKBase;
using VRC.Udon;

/// <summary>
/// OpForCaptain: decides where the robots go and when waves launch. No sync (sync mode: None).
/// It only does anything on the Game Host's client (the Host's client owns and runs all robot AI anyway).
///
/// LOCATION DATA ("intel")
///  One entry per roster slot (6): the last place a robot spotted that player.
///  - Filled ONLY by ReportSighting(), which Chasers / Droppers / Spotters call when they see a player.
///  - Wiped when the player is killed, leaves, takes another slot, is inside Point B's radius (goalRadius),
///    or (optional) when it is older than intelExpirySeconds. Everything is wiped when difficulty is set to 0.
///  - Nothing launches until there is intel.
///
/// TARGET = the intel closest to Point B (the player furthest ahead). Stragglers are not hunted.
///
/// FREE robots (Travel or Patrol) are sent to the target:
///  - when the leader changes (another player is now closest to B): all free robots are re-sent
///  - otherwise only robots whose current destination / patrol center is more than resendDistance (30 m) from the target
///  Robots in Chase / Dive (Chasers) or Attack / Trail (Droppers: bombing or has bombed) ignore orders.
///
/// WAVES: while there is a target, every waveCooldown seconds (global, any LaunchArea) the Captain launches
///  whatever is parked, up to chasersPerWave Chasers + droppersPerWave Droppers, from a RANDOM LaunchArea.
///  One shared pool: parked robots wait at their own start position; at launch they are teleported to the chosen LaunchArea.
///  Pool size = difficulty x one wave (difficulty 1 = 2 Chasers + 1 Dropper, difficulty 5 = 10 Chasers + 5 Droppers).
///
/// Exploded robots are sent back to their parking spot after robotRespawnTime seconds.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class OpForCaptain : UdonSharpBehaviour
{
    [Header("Scene references")]
    [SerializeField] private GameManager gameManager;
    [Tooltip("The SAME 6 PlayerSlot objects the PlayerRoster uses (any order is fine, but keep it stable).")]
    [SerializeField] private PlayerSlot[] slots;
    [SerializeField] private Transform pointB;
    [Tooltip("2 or more LaunchArea objects. Must be on / near the NavMesh.")]
    [SerializeField] private Transform[] launchAreas;
    [Tooltip("All Chaser drones (each one needs its 'Captain' field set to this object).")]
    [SerializeField] private DroneChaser[] chasers;
    [Tooltip("All Dropper drones (each one needs its 'Captain' field set to this object). Can be empty.")]
    [SerializeField] private DroneDropper[] droppers;

    [Header("Waves")]
    [SerializeField] private int chasersPerWave = 2;
    [Tooltip("Droppers per wave. 0 = no Droppers.")]
    [SerializeField] private int droppersPerWave = 1;
    [Tooltip("Seconds between waves, from ANY LaunchArea.")]
    [SerializeField] private float waveCooldown = 5f;
    [Tooltip("Robots of one wave appear this far from the LaunchArea center, so they don't stack.")]
    [SerializeField] private float launchSpread = 2f;
    [Tooltip("An exploded robot goes back to its parking spot after this many seconds.")]
    [SerializeField] private float robotRespawnTime = 20f;

    [Header("Targeting")]
    [Tooltip("A free robot is only re-sent when the target is further than this from where it is heading / circling.")]
    [SerializeField] private float resendDistance = 5f;
    [Tooltip("Players this close to Point B (horizontal) are done: their intel is wiped and ignored.")]
    [SerializeField] private float goalRadius = 10f;
    [Tooltip("Intel older than this is dropped. 0 = never expires.")]
    [SerializeField] private float intelExpirySeconds = 0f;

    [Header("Timing")]
    [SerializeField] private float tickInterval = 0.25f;

    [Header("Debug")]
    [SerializeField] private bool debugLog;
    [Tooltip("TEST ONLY, stand-in for a Spotter: every debugSightInterval seconds all playing players are 'spotted' at their real position.")]
    [SerializeField] private bool debugSightAllPlayers;
    [SerializeField] private float debugSightInterval = 5f;

    // ---- intel (one entry per slot) ----
    private bool[] hasIntel;
    private Vector3[] intelPos;
    private float[] intelTime;
    private int[] intelPlayerId;

    // ---- target ----
    private int leaderSlot = -1;
    private Vector3 targetPos;

    // ---- timers / bookkeeping ----
    private float[] respawnTimer;
    private float[] dropperRespawnTimer;
    private float waveTimer;
    private float tickAccum;
    private float debugSightTimer;
    private bool wasHost;
    private int lastDifficulty = -1;
    private bool ready;

    void Start()
    {
        if (slots == null || slots.Length == 0) { Debug.LogWarning("[Captain] no PlayerSlots assigned"); return; }
        if (gameManager == null) { Debug.LogWarning("[Captain] GameManager not assigned"); return; }
        if (pointB == null) { Debug.LogWarning("[Captain] Point B not assigned"); return; }
        if (launchAreas == null || launchAreas.Length == 0) Debug.LogWarning("[Captain] no LaunchAreas assigned");
        if (chasers == null) chasers = new DroneChaser[0];
        if (droppers == null) droppers = new DroneDropper[0];

        hasIntel = new bool[slots.Length];
        intelPos = new Vector3[slots.Length];
        intelTime = new float[slots.Length];
        intelPlayerId = new int[slots.Length];

        respawnTimer = new float[chasers.Length];
        for (int i = 0; i < respawnTimer.Length; i++) respawnTimer[i] = -1f;

        dropperRespawnTimer = new float[droppers.Length];
        for (int i = 0; i < dropperRespawnTimer.Length; i++) dropperRespawnTimer[i] = -1f;

        ready = true;
    }

    void Update()
    {
        if (!ready) return;

        tickAccum += Time.deltaTime;
        if (tickAccum < tickInterval) return;
        float step = tickAccum;
        tickAccum = 0f;
        Tick(step);
    }

    // ================= main loop (runs a few times a second) =================

    void Tick(float step)
    {
        bool host = gameManager.IsGameHost();
        if (!host)
        {
            // Lost (or never had) the Game Host role: the intel stays with the Host only.
            if (wasHost) { ClearAllIntel(); wasHost = false; }
            return;
        }

        if (!wasHost)
        {
            // Just became Game Host: start clean (v1 does not hand intel over).
            wasHost = true;
            lastDifficulty = -1;
            ClearAllIntel();
            waveTimer = 0f;
            if (debugLog) Debug.Log("[Captain] I am the Game Host now");
        }

        int diff = gameManager.GetDifficulty();
        if (diff != lastDifficulty)
        {
            lastDifficulty = diff;
            if (diff == 0) ResetAll();
        }
        if (diff == 0) return;

        if (waveTimer > 0f) waveTimer -= step;

        if (debugSightAllPlayers) DebugSightPlayers(step);

        UpdateIntel();
        UpdateRespawns(step);

        bool leaderChanged = RecomputeLeader();
        if (leaderSlot < 0) return;     // no intel: nothing is sent, nothing launches

        Redirect(leaderChanged);
        if (waveTimer <= 0f) TryLaunchWave(diff);
    }

    // ================= intel =================

    /// <summary>
    /// Robots call this when they see a player (Chaser: DroneChaser does it while chasing; Dropper / Spotter later).
    /// Only has an effect on the Game Host's client, for playing + alive players, outside Point B's radius.
    /// </summary>
    public void ReportSighting(VRCPlayerApi player, Vector3 pos)
    {
        if (!ready || !Utilities.IsValid(player)) return;
        if (!gameManager.IsGameHost() || gameManager.GetDifficulty() <= 0) return;

        int slot = FindSlot(player);
        if (slot < 0) return;                       // observers are never tracked
        if (!slots[slot].IsAlive()) return;
        if (InsideGoal(pos) || InsideGoal(player.GetPosition())) return;

        hasIntel[slot] = true;
        intelPos[slot] = pos;
        intelTime[slot] = Time.time;
        intelPlayerId[slot] = player.playerId;

        if (debugLog) Debug.Log("[Captain] intel slot " + slot + " at " + pos);
    }

    int FindSlot(VRCPlayerApi player)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].IsOccupiedBy(player)) return i;
        }
        return -1;
    }

    // Wipes the intel of players who were killed, left, swapped slot, reached Point B, or whose intel is too old.
    void UpdateIntel()
    {
        for (int i = 0; i < slots.Length; i++)
        {
            if (!hasIntel[i]) continue;

            VRCPlayerApi p = slots[i].GetOccupant();
            if (p == null || !slots[i].IsAlive() || slots[i].GetRawOccupantId() != intelPlayerId[i])
            {
                WipeIntel(i, "killed / left");
            }
            else if (InsideGoal(p.GetPosition()))
            {
                WipeIntel(i, "reached Point B");
            }
            else if (intelExpirySeconds > 0f && Time.time - intelTime[i] > intelExpirySeconds)
            {
                WipeIntel(i, "expired");
            }
        }
    }

    void WipeIntel(int slot, string why)
    {
        hasIntel[slot] = false;
        if (debugLog) Debug.Log("[Captain] intel wiped, slot " + slot + " (" + why + ")");
    }

    void ClearAllIntel()
    {
        if (hasIntel != null)
        {
            for (int i = 0; i < hasIntel.Length; i++) hasIntel[i] = false;
        }
        leaderSlot = -1;
    }

    bool InsideGoal(Vector3 p)
    {
        return pointB != null && HorizDistance(p, pointB.position) <= goalRadius;
    }

    // Picks the intel closest to Point B. Returns true when a DIFFERENT player is now the target.
    bool RecomputeLeader()
    {
        int best = -1;
        float bestD = 100000f;
        Vector3 b = pointB.position;

        for (int i = 0; i < slots.Length; i++)
        {
            if (!hasIntel[i]) continue;
            float d = HorizDistance(intelPos[i], b);
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }

        bool changed = best != leaderSlot;
        leaderSlot = best;
        if (best >= 0) targetPos = intelPos[best];

        if (changed && debugLog) Debug.Log("[Captain] target is now slot " + best);
        return changed && best >= 0;
    }

    // ================= sending robots =================

    // Free robots (Travel / Patrol) go to the target. Chasing, diving, attacking, trailing and exploded robots say no by themselves.
    void Redirect(bool force)
    {
        for (int i = 0; i < chasers.Length; i++)
        {
            DroneChaser c = chasers[i];
            if (c == null || !c.IsFreeForOrders()) continue;

            if (force || HorizDistance(c.GetCommandAnchor(), targetPos) > resendDistance)
            {
                c.CommandTravel(targetPos);
                if (debugLog) Debug.Log("[Captain] re-sent chaser " + i);
            }
        }

        for (int i = 0; i < droppers.Length; i++)
        {
            DroneDropper d = droppers[i];
            if (d == null || !d.IsFreeForOrders()) continue;

            if (force || HorizDistance(d.GetCommandAnchor(), targetPos) > resendDistance)
            {
                d.CommandTravel(targetPos);
                if (debugLog) Debug.Log("[Captain] re-sent dropper " + i);
            }
        }
    }

    // One wave = whatever is parked (up to chasersPerWave Chasers + droppersPerWave Droppers), from a random LaunchArea.
    // The cooldown only starts when something actually launched.
    void TryLaunchWave(int difficulty)
    {
        if (launchAreas == null || launchAreas.Length == 0) return;

        int allowedChasers = Mathf.Min(chasers.Length, difficulty * chasersPerWave);
        int allowedDroppers = Mathf.Min(droppers.Length, difficulty * droppersPerWave);

        int parked = 0;
        for (int i = 0; i < allowedChasers; i++)
        {
            if (chasers[i] != null && chasers[i].IsParked()) parked++;
        }
        for (int i = 0; i < allowedDroppers; i++)
        {
            if (droppers[i] != null && droppers[i].IsParked()) parked++;
        }
        if (parked == 0) return;

        Transform area = launchAreas[Random.Range(0, launchAreas.Length)];
        if (area == null) return;

        int launched = 0;   // robots launched so far: also the spawn slot around the LaunchArea
        int launchedChasers = 0;
        for (int i = 0; i < allowedChasers && launchedChasers < chasersPerWave; i++)
        {
            DroneChaser c = chasers[i];
            if (c == null || !c.IsParked()) continue;

            c.LaunchFrom(SpawnPoint(area, launched), targetPos);
            launchedChasers++;
            launched++;
        }

        int launchedDroppers = 0;
        for (int i = 0; i < allowedDroppers && launchedDroppers < droppersPerWave; i++)
        {
            DroneDropper d = droppers[i];
            if (d == null || !d.IsParked()) continue;

            d.LaunchFrom(SpawnPoint(area, launched), targetPos);
            launchedDroppers++;
            launched++;
        }

        waveTimer = waveCooldown;
        if (debugLog) Debug.Log("[Captain] wave launched from " + area.name + ": " + launchedChasers + " chaser(s), " + launchedDroppers + " dropper(s)");
    }

    Vector3 SpawnPoint(Transform area, int index)
    {
        float ang = index * 2.0944f;   // 120 degrees apart
        Vector3 raw = area.position + new Vector3(Mathf.Sin(ang), 0f, Mathf.Cos(ang)) * launchSpread;

        NavMeshHit navHit;
        if (NavMesh.SamplePosition(raw, out navHit, 6f, -1)) return navHit.position;
        return area.position;
    }

    // ================= robots that exploded / game stopped =================

    void UpdateRespawns(float step)
    {
        for (int i = 0; i < chasers.Length; i++)
        {
            DroneChaser c = chasers[i];
            if (c == null) continue;

            if (!c.IsExploded())
            {
                respawnTimer[i] = -1f;
                continue;
            }

            if (respawnTimer[i] < 0f) respawnTimer[i] = robotRespawnTime;
            respawnTimer[i] -= step;
            if (respawnTimer[i] <= 0f)
            {
                respawnTimer[i] = -1f;
                c.ResetToHome();
                if (debugLog) Debug.Log("[Captain] chaser " + i + " back in the pool");
            }
        }

        for (int i = 0; i < droppers.Length; i++)
        {
            DroneDropper d = droppers[i];
            if (d == null) continue;

            if (!d.IsExploded())
            {
                dropperRespawnTimer[i] = -1f;
                continue;
            }

            if (dropperRespawnTimer[i] < 0f) dropperRespawnTimer[i] = robotRespawnTime;
            dropperRespawnTimer[i] -= step;
            if (dropperRespawnTimer[i] <= 0f)
            {
                dropperRespawnTimer[i] = -1f;
                d.ResetToHome();
                if (debugLog) Debug.Log("[Captain] dropper " + i + " back in the pool");
            }
        }
    }

    // Difficulty 0: everything resets. Robots go back to their parking spot, intel is gone.
    void ResetAll()
    {
        ClearAllIntel();
        waveTimer = 0f;

        for (int i = 0; i < chasers.Length; i++)
        {
            respawnTimer[i] = -1f;
            DroneChaser c = chasers[i];
            if (c != null && !c.IsParked()) c.ResetToHome();
        }

        for (int i = 0; i < droppers.Length; i++)
        {
            dropperRespawnTimer[i] = -1f;
            DroneDropper d = droppers[i];
            if (d != null && !d.IsParked()) d.ResetToHome();
        }
        if (debugLog) Debug.Log("[Captain] game stopped: everything reset");
    }

    // ================= debug =================

    // Stand-in for a Spotter while none exists. Uses the players' REAL positions.
    void DebugSightPlayers(float step)
    {
        debugSightTimer -= step;
        if (debugSightTimer > 0f) return;
        debugSightTimer = debugSightInterval;

        for (int i = 0; i < slots.Length; i++)
        {
            VRCPlayerApi p = slots[i].GetOccupant();
            if (p == null) continue;
            ReportSighting(p, p.GetPosition());
        }
    }

    // ================= helpers =================

    float HorizDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
