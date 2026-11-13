using UdonSharp;
using UnityEngine;

/// Put this on the map mesh. The camera stays disabled; the script renders it on demand.
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class MapCameraRender : UdonSharpBehaviour
{
    [SerializeField] private Camera mapCamera;
    [Tooltip("Seconds between renders. 0 = render once and never again.")]
    [SerializeField] private float interval = 10f;
    [Tooltip("Wait a moment after world load so everything is in place before the first render.")]
    [SerializeField] private float firstRenderDelay = 2f;

    void Start()
    {
        if (mapCamera == null) return;
        mapCamera.enabled = false;
        SendCustomEventDelayedSeconds(nameof(RenderNow), firstRenderDelay);
    }

    public void RenderNow()
    {
        mapCamera.Render();
        if (interval > 0f) SendCustomEventDelayedSeconds(nameof(RenderNow), interval);
    }
}