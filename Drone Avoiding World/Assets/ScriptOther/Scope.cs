using UdonSharp;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Zoom of the scope sight. The scope is now a child of the gun, so it is no pickup of its own:
/// the camera and the lens are switched on and off by TwoHandGun (scope selected + held by the local player),
/// NOT here. This script only sets the field of view from the zoom slider.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class Scope : UdonSharpBehaviour
{
    [Header("References")]
    public Camera scopeCamera;
    public Slider zoomSlider;

    [Header("Zoom")]
    public float baseFov = 20f;     // camera FOV at 1x, tune to taste
    public float minZoom = 1f;
    public float maxZoom = 6f;

    void Start()
    {
        ApplyZoom();
    }

    // Called by the slider's On Value Changed event
    public void OnZoomChanged()
    {
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (scopeCamera == null) return;

        float zoom = minZoom;
        if (zoomSlider != null)
        {
            zoom = Mathf.Clamp(zoomSlider.value, minZoom, maxZoom);
        }

        // True optical zoom: divide the tangent of the half-angle, not the angle itself
        float halfBase = baseFov * 0.5f * Mathf.Deg2Rad;
        scopeCamera.fieldOfView = 2f * Mathf.Atan(Mathf.Tan(halfBase) / zoom) * Mathf.Rad2Deg;
    }
}
