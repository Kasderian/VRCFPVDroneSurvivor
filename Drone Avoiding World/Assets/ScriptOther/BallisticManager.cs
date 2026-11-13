using UdonSharp;
using UnityEngine;

// One instance in the scene (GameObject named "BallisticManager").
// Only simulates bullets spawned by THIS client; nothing here is networked.
//
// Spawn()       : normal bullet (rifle): gravity only, lives until it hits something or maxLifetime.
// SpawnPellet() : shotgun pellet: gravity + a speed profile that depends on the distance travelled.
//                 Speed at 0 m = the spawn speed (the gun's muzzle velocity), then it is interpolated linearly
//                 through the (distance, speed) points below. The pellet vanishes at the LAST distance.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class BallisticManager : UdonSharpBehaviour
{
    [Header("Ballistics")]
    public int maxBullets = 64;
    public float gravity = 9.81f;
    public float maxLifetime = 2f;          // seconds; 900 m/s * 2 s = 1800 m, plenty for a 500 m map

    [Header("Shotgun pellets (speed falls with distance travelled)")]
    [Tooltip("Distances in meters, ascending. Pellets vanish at the LAST one (80-120 m).")]
    public float[] pelletProfileDist = new float[] { 10f, 40f, 100f };
    [Tooltip("Speed in m/s at each distance above (same number of entries).")]
    public float[] pelletProfileSpeed = new float[] { 300f, 200f, 100f };

    [Header("Hit Detection")]
    public LayerMask hitMask;               // set to Default/Environment/robot layers. NOT Pickup, Player, PlayerLocal, UI
    public string hitEventName = "OnBulletHit";
    public bool debugLog = true;            // turn OFF for the shotgun: it logs every pellet

    private Vector3[] positions;
    private Vector3[] velocities;
    private float[] ages;
    private bool[] active;
    private bool[] isPellet;
    private float[] traveled;
    private float[] startSpeed;
    private int nextSlot;
    private int profileCount;
    private float pelletRange = 100000f;

    void Start()
    {
        positions = new Vector3[maxBullets];
        velocities = new Vector3[maxBullets];
        ages = new float[maxBullets];
        active = new bool[maxBullets];
        isPellet = new bool[maxBullets];
        traveled = new float[maxBullets];
        startSpeed = new float[maxBullets];
        nextSlot = 0;

        profileCount = 0;
        if (pelletProfileDist != null && pelletProfileSpeed != null)
        {
            profileCount = Mathf.Min(pelletProfileDist.Length, pelletProfileSpeed.Length);
        }
        if (profileCount > 0) pelletRange = pelletProfileDist[profileCount - 1];
    }

    // Called by the gun on the shooter's client only. Normal bullet.
    public void Spawn(Vector3 position, Vector3 velocity)
    {
        Launch(position, velocity, false);
    }

    // Called by the shotgun on the shooter's client only. One call per pellet.
    public void SpawnPellet(Vector3 position, Vector3 velocity)
    {
        Launch(position, velocity, true);
    }

    private void Launch(Vector3 position, Vector3 velocity, bool pellet)
    {
        if (positions == null) return;

        int i = nextSlot;                       // ring buffer: oldest bullet gets overwritten if full
        nextSlot = (nextSlot + 1) % maxBullets;
        if (debugLog) Debug.Log("[Ballistics] spawn pos=" + position + " vel=" + velocity);
        positions[i] = position;
        velocities[i] = velocity;
        ages[i] = 0f;
        traveled[i] = 0f;
        startSpeed[i] = velocity.magnitude;
        isPellet[i] = pellet;
        active[i] = true;
    }

    // Pellet speed after 'dist' meters: from startSpeed at 0 m through the profile points.
    private float ProfileSpeed(float dist, float start)
    {
        float prevD = 0f;
        float prevS = start;
        for (int k = 0; k < profileCount; k++)
        {
            float d = pelletProfileDist[k];
            float s = pelletProfileSpeed[k];
            if (dist <= d)
            {
                float span = d - prevD;
                float t = (span > 0.0001f) ? (dist - prevD) / span : 1f;
                return Mathf.Lerp(prevS, s, t);
            }
            prevD = d;
            prevS = s;
        }
        return prevS;
    }

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        Vector3 g = new Vector3(0f, -gravity, 0f);

        for (int i = 0; i < maxBullets; i++)
        {
            if (!active[i]) continue;

            Vector3 p0 = positions[i];
            Vector3 v0 = velocities[i];

            // constant-acceleration step over this frame
            Vector3 p1 = p0 + v0 * dt + g * (0.5f * dt * dt);
            Vector3 v1 = v0 + g * dt;

            Vector3 seg = p1 - p0;
            float len = seg.magnitude;
            if (len > 0.0001f)
            {
                RaycastHit hit;
                if (Physics.Raycast(p0, seg / len, out hit, len, hitMask, QueryTriggerInteraction.Ignore))
                {
                    active[i] = false;
                    HandleHit(hit.collider, hit.point);
                    continue;
                }
            }

            positions[i] = p1;
            ages[i] += dt;
            if (ages[i] > maxLifetime)
            {
                active[i] = false;
                continue;
            }

            if (isPellet[i])
            {
                traveled[i] += len;
                if (traveled[i] >= pelletRange)
                {
                    active[i] = false;
                    continue;
                }

                // gravity has bent the direction; the speed itself follows the profile (a slow pellet drops more)
                float m = v1.magnitude;
                if (m > 0.01f && profileCount > 0)
                {
                    v1 = v1 * (ProfileSpeed(traveled[i], startSpeed[i]) / m);
                }
            }

            velocities[i] = v1;
        }
    }

    private void HandleHit(Collider col, Vector3 point)
    {
        if (col == null) return;

        if (debugLog)
        {
            Debug.Log("[Ballistics] hit " + col.gameObject.name + " at " + point);
        }

        // Local call on the shooter's client. The target decides what to network
        // (test cube: send to All; robots later: send to their owner / Game Host).
        UdonSharpBehaviour receiver = col.GetComponentInParent<UdonSharpBehaviour>();
        if (receiver != null)
        {
            receiver.SendCustomEvent(hitEventName);
        }
    }
}
