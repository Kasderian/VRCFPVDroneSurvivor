using UdonSharp;
using UnityEngine;
using VRC.Udon.Common.Interfaces;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class TestTarget : UdonSharpBehaviour
{
    public Renderer targetRenderer;
    public Material hitMaterial;
    public float flashSeconds = 2f;

    private Material originalMaterial;
    private float restoreTime = -1f;
    private float lockUntil;                 // shooter's client: one network event per blast (shotgun pellets)

    void Start()
    {
        if (targetRenderer == null) targetRenderer = GetComponent<Renderer>();
        originalMaterial = targetRenderer.sharedMaterial;
    }

    // Called LOCALLY on the shooter's client by BallisticManager after its raycast hit this object.
    public void OnBulletHit()
    {
        if (Time.time < lockUntil) return;
        lockUntil = Time.time + 0.25f;
        Debug.Log("[Target] OnBulletHit"); // and "[Target] ShowHit" in ShowHit()
        SendCustomNetworkEvent(NetworkEventTarget.All, nameof(ShowHit));
    }

    // Runs on every client (including the shooter).
    public void ShowHit()
    {
        Debug.Log("[Target] OnBulletHit"); // and "[Target] ShowHit" in ShowHit()
        targetRenderer.sharedMaterial = hitMaterial;
        restoreTime = Time.time + flashSeconds;   // another hit just extends the timer
    }

    void Update()
    {
        if (restoreTime > 0f && Time.time >= restoreTime)
        {
            targetRenderer.sharedMaterial = originalMaterial;
            restoreTime = -1f;
        }
    }
}