using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class ZoomFOV : MonoBehaviour
{
    [Header("XR Rig")]
    [Tooltip("The XR rig / Locomotion object whose scale gets reduced to produce the " +
             "zoom effect.  Leave blank to auto-find a GameObject named 'Locomotion'.\n\n" +
             "Why scale instead of changing Camera.fieldOfView: in XR, Unity overwrites " +
             "the camera's projection matrix every frame from the HMD's intrinsics, so " +
             "Camera.fieldOfView changes have no visible effect on-device (the XR Device " +
             "Simulator in editor doesn't push real intrinsics, which is why FOV-based " +
             "zoom appeared to work in Play mode but did nothing on the Quest).  Scaling " +
             "the rig down (everything in the world stays the same actual size) is the " +
             "standard VR 'scope zoom' workaround.")]
    public Transform xrRig;

    [Header("Zoom Amount")]
    [Tooltip("Rig scale when NOT zoomed.  Almost always 1.")]
    public float normalRigScale = 1f;

    [Tooltip("Rig scale when zoomed in.  Smaller = stronger zoom.  0.33 ≈ 3× magnification " +
             "(matches the old 60→20° FOV change).  Don't go below ~0.1 — the rig gets so " +
             "small that head-translation tracking becomes uncomfortably twitchy.")]
    [Range(0.1f, 1f)]
    public float zoomedRigScale = 0.33f;

    [Tooltip("Higher = snappier transition between normal and zoomed.")]
    public float zoomSpeed = 10f;

    [Range(0.1f, 1f)]
    public float triggerThreshold = 0.5f;

    private RawImage   vignetteImage;
    private float      vignetteAlpha = 0f;

    // We drive `currentFOV` directly instead of reading Camera.fieldOfView
    // each frame, because in VR Camera.fieldOfView is a no-op — the XR
    // runtime owns the projection.  Tracking it ourselves keeps the lerp
    // working identically in editor and on headset.
    private float      currentFOV;

    // Per-eye base projection matrices, snapshotted from the XR runtime once
    // it has populated real values.  Camera.fieldOfView has no effect in VR,
    // so to zoom we have to scale the m00/m11 (focal-length) terms of the
    // per-eye matrices each frame.  Caching the originals here means we can
    // re-apply our zoom factor cleanly without stomping the runtime's per-eye
    // asymmetry / IPD with whatever matrix we set last frame.
    private Matrix4x4  baseLeftProj;
    private Matrix4x4  baseRightProj;
    private bool       baseProjsCached;

    // Use the same InputAction pattern as GuessManager / GlobeInteractable so
    // controller bindings stay consistent across the project.  XRController
    // {LeftHand}/{TriggerButton} resolves to the analog left-trigger pull on
    // any OpenXR-style controller (Quest, Index, etc.).
    private InputAction leftTriggerAction;

    void OnEnable()
    {
        leftTriggerAction = new InputAction("LeftTrigger",
            binding: "<XRController>{LeftHand}/{TriggerButton}");
        leftTriggerAction.Enable();
    }

    void OnDisable()
    {
        leftTriggerAction?.Disable();
    }

    void Start()
    {
        // Auto-find the rig if the user didn't drag one in.
        //
        // IMPORTANT: do NOT use the "Locomotion" GameObject here, even though
        // GuessManager grabs that one — Locomotion is the locomotion-provider
        // object (continuous-move / teleport), it's NOT necessarily the parent
        // of the XR camera.  Scaling it has no effect on what the user sees.
        //
        // What we actually want to scale is the XR Origin / rig, which is an
        // ancestor of Camera.main.  Walking up the Main Camera's parent chain
        // and grabbing the topmost transform that contains the camera is a
        // reliable way to find it without hardcoding hierarchy names.
        if (xrRig == null)
        {
            // Prefer common XRI / OVR rig names if present.
            GameObject rig = GameObject.Find("XR Origin (XR Rig)");
            if (rig == null) rig = GameObject.Find("XR Origin");
            if (rig == null) rig = GameObject.Find("XROrigin");
            if (rig == null) rig = GameObject.Find("OVRCameraRig");
            if (rig != null) xrRig = rig.transform;
        }

        // Fallback: walk up from Main Camera to its top-most transform.  Works
        // for any rig prefab without us needing to know its name.  We pick the
        // topmost ancestor (rather than the immediate parent / Camera Offset)
        // because some rigs apply scale at the Origin level and reset Camera
        // Offset to identity each frame — scaling the Origin always wins.
        if (xrRig == null && Camera.main != null)
        {
            Transform t = Camera.main.transform;
            while (t.parent != null) t = t.parent;
            xrRig = t;
            Debug.Log($"ZoomFOV: auto-found XR rig by walking up from Camera.main → '{t.name}'.");
        }

        if (xrRig == null)
            Debug.LogWarning("ZoomFOV: could not locate an XR rig — zoom will be a no-op. " +
                             "Drag your XR Origin into the 'Xr Rig' field on this component.");

        CreateVignette();
    }

    bool IsZoomTriggered()
    {
        // Hard-disable while the player is in guess mode — the zoom feature is
        // for inspecting the Street View skybox, not the globe UI, and the
        // left trigger / left-hand controls are reserved for guess interactions
        // (X button toggle, left thumbstick zoom on the globe) during that phase.
        if (GuessManager.Instance != null && GuessManager.Instance.IsGuessing)
            return false;

        if (leftTriggerAction == null) return false;
        return leftTriggerAction.ReadValue<float>() >= triggerThreshold;
    }

    void Update()
    {
        bool isZooming = IsZoomTriggered();

        // Vignette fade — independent of rig scale so the visual cue still
        // appears even if the rig reference happens to be missing.
        float targetAlpha = isZooming ? 1f : 0f;
        vignetteAlpha = Mathf.Lerp(vignetteAlpha, targetAlpha, zoomSpeed * Time.deltaTime);
        if (vignetteImage != null)
            vignetteImage.color = new Color(1, 1, 1, vignetteAlpha);

        // Rig scale — this is what actually produces the zoom on-device.  When
        // the rig is disabled (guess mode), Unity ignores localScale changes
        // until it's re-enabled, but our IsZoomTriggered guard already returns
        // false in that state so the rig will lerp back to normalRigScale as
        // soon as it wakes up.
        if (xrRig != null)
        {
            float target  = isZooming ? zoomedRigScale : normalRigScale;
            float current = xrRig.localScale.x;
            float lerped  = Mathf.Lerp(current, target, zoomSpeed * Time.deltaTime);
            xrRig.localScale = Vector3.one * lerped;
        }
    }

    void CreateVignette()
    {
        GameObject canvasObj = new GameObject("VignetteCanvas");
        Canvas canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 999;
        canvasObj.AddComponent<CanvasScaler>();
        canvasObj.AddComponent<GraphicRaycaster>();

        GameObject imgObj = new GameObject("VignetteImage");
        imgObj.transform.SetParent(canvasObj.transform, false);
        vignetteImage = imgObj.AddComponent<RawImage>();

        RectTransform rect = imgObj.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;

        int size = 512;
        Texture2D tex = new Texture2D(size, size);
        float center = size / 2f;
        float radius = size * 0.38f;
        float softEdge = size * 0.06f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dist = Vector2.Distance(new Vector2(x, y), new Vector2(center, center));
                float alpha;

                if (dist < radius - softEdge)
                    alpha = 0f;
                else if (dist < radius + softEdge)
                    alpha = Mathf.InverseLerp(radius - softEdge, radius + softEdge, dist);
                else
                    alpha = 1f;

                tex.SetPixel(x, y, new Color(0, 0, 0, alpha));
            }
        }

        tex.Apply();
        vignetteImage.texture = tex;
        vignetteImage.color = new Color(1, 1, 1, 0);
    }
}