using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

public class GlobeInteractable : MonoBehaviour
{
    [Header("Interaction")]
    public float spinSensitivity = 150f; // degrees per second at full stick deflection
    public float zoomSpeed       = 0.5f;
    public float minScale        = 0.3f;
    public float maxScale        = 1.8f;

    [Tooltip("Globe scale applied by ResetGlobe (i.e. when guess mode opens).  Should " +
             "sit between minScale and maxScale.  Tune so the globe is comfortably " +
             "readable the moment the panel appears.")]
    public float initialScale    = 1.2f;

    [Header("Grab Controls (squeeze grip on either controller)")]
    [Tooltip("Degrees of globe rotation per meter of controller motion while gripping. " +
             "Higher = the globe responds more aggressively to small hand movements. " +
             "Default 250 ≈ a 40 cm sweep produces a 100° spin.")]
    public float grabSpinSensitivity = 250f;

    [Tooltip("Scale-units of zoom per meter of pull-apart while BOTH grips are held. " +
             "1.0 = pulling your hands 30 cm farther apart adds 0.30 to the globe's scale. " +
             "Final scale is still clamped to [minScale, maxScale].")]
    public float grabZoomSensitivity = 1.0f;

    [Tooltip("Minimum grip-axis value (0–1) considered 'gripping'.  0.5 means the user " +
             "has to squeeze the controller's grip at least halfway before grab kicks in.")]
    [Range(0.05f, 1f)]
    public float gripActivationThreshold = 0.5f;

    [Tooltip("Invert horizontal grab direction.  Off = dragging your hand right spins the " +
             "globe so its front face follows your hand to the right (feels like grabbing " +
             "a real globe).  On = mirrors that.")]
    public bool invertGrabSpinX = false;

    [Tooltip("Invert vertical grab direction.  Off = dragging your hand up tilts the globe's " +
             "top toward you (feels like grabbing a real globe).")]
    public bool invertGrabSpinY = false;

    [Header("Results Reveal Animation")]
    [Tooltip("Globe scale during the zoom-in phase right after the user confirms a guess. " +
             "Should be larger than the eventual results scale so the marker is clearly framed.")]
    public float zoomedInScale            = 1.5f;

    [Tooltip("Seconds to spend zooming in and centering on the guess marker.")]
    public float zoomToGuessDuration      = 0.6f;

    [Tooltip("Seconds to hold the zoomed-in shot of the guess marker before flying " +
             "toward the correct location.")]
    public float pauseAtGuessDuration     = 0.5f;

    [Tooltip("Seconds to spend spinning the globe from the guess marker to the correct " +
             "location while drawing the dotted line.")]
    public float spinToCorrectDuration    = 1.5f;

    [Tooltip("Seconds to hold once the correct marker has been revealed before shrinking " +
             "the globe to its results-panel size.")]
    public float pauseAtCorrectDuration   = 1.0f;

    [Tooltip("Seconds to spend shrinking the globe back to the results-panel size.")]
    public float shrinkToResultsDuration  = 0.6f;

    [Header("Arc Line (dashed, drawn during reveal)")]
    [Tooltip("Optional prefab used as a single dash segment along the great-circle arc " +
             "between the guess and correct markers.  If null, small unlit cubes are " +
             "created at runtime.  The dash's local +Z is treated as its 'along the arc' " +
             "axis — design the prefab the same way.")]
    public GameObject arcDashPrefab;

    [Tooltip("Number of dashes placed along the arc.  More dashes = finer line.  " +
             "Combined with arcDashFill, this controls the dash/gap pattern: fewer " +
             "dashes with arcDashFill ≈ 0.5 gives a chunky morse-code look, many dashes " +
             "with arcDashFill ≈ 0.9 looks almost continuous.")]
    public int   arcDashCount        = 18;

    [Tooltip("Fraction of each segment occupied by the dash (the rest is gap).  " +
             "0.5 = equal dash + gap.  0.9 ≈ near-continuous line.  0.2 ≈ sparse dots.")]
    [Range(0.05f, 1f)]
    public float arcDashFill         = 0.55f;

    [Tooltip("Cross-section thickness of each dash, in globe-local units.  Higher = " +
             "fatter line.  Tune alongside arcDashRadialOffset so the line floats just " +
             "above the surface without z-fighting.")]
    public float arcDashThickness    = 0.008f;

    [Tooltip("Color of procedurally-created arc dashes.  Ignored if arcDashPrefab is assigned.")]
    public Color arcDashColor        = Color.white;

    [Tooltip("Radial offset above the globe surface, as a multiplier on globe radius. " +
             "1.0 = sitting on the surface; 1.01 = floating just above so the line doesn't " +
             "z-fight with the globe texture.")]
    public float arcDashRadialOffset = 1.012f;

    [Header("Grab Momentum (real-globe inertia)")]
    [Tooltip("How fast the globe coasts to a stop after release.  Larger = stops faster. " +
             "It's an exponential decay: every 1/friction seconds the speed drops by ~63%. " +
             "0 = no friction (globe spins forever — good for testing, not for play).")]
    public float grabMomentumFriction  = 1.5f;

    [Tooltip("Below this rotational speed (deg/sec) the coast is considered finished and " +
             "momentum snaps to zero.  Stops imperceptible drift after a long decay tail.")]
    public float grabMomentumMinSpeed  = 2f;

    [Tooltip("Time constant (seconds) for smoothing the live grab velocity.  The release " +
             "velocity is the smoothed value at the moment of release — this prevents a " +
             "single jittery final frame from launching the globe at the wrong speed. " +
             "0 = use raw last-frame velocity (twitchy).  0.15 ≈ averages over the last ~150ms.")]
    public float grabVelocitySmoothing = 0.15f;

    [Tooltip("Optional pivot for zoom.  If set, the LEFT thumbstick scales THIS transform " +
             "instead of the globe itself.  Place an empty GameObject at the BACK of the globe " +
             "(the side away from the user), parent the globe under it, then drag it here.  " +
             "Result: the back of the globe stays anchored in space while the front grows " +
             "toward the user as you zoom in.\n\n" +
             "IMPORTANT: keep the GlobeInteractable script on the GLOBE itself, NOT on the " +
             "pivot.  The script reads marker coordinates from the globe's transform, so " +
             "moving it would break lat/lng calculations.  Leaving it on the globe and only " +
             "reaching out to scale the pivot keeps coordinate math identical.")]
    public Transform zoomPivot;

    [Header("Markers")]
    // Assign the GuessPin prefab here (red pin: sphere head + skinny cylinder stem).
    // Prefab convention: built along its local +Y axis with the sphere at the top
    // and the cylinder extending downward (toward -Y).  Pivot should sit at the
    // tip of the cylinder so the pin plants flush into the globe surface.
    public GameObject markerPrefab;         // red — player's guess   (GuessPin)
    // Assign the CorrectPin prefab here (green).  Same Y-axis convention as above.
    public GameObject correctMarkerPrefab;  // green — correct answer (CorrectPin)

    [Header("Audio")]
    [Tooltip("Plays once when the player places their guess marker on the globe.")]
    public AudioClip markerPlacedClip;
    [Tooltip("Loops while the dashed arc is being drawn during the results reveal. " +
             "Stopped automatically when the arc finishes drawing.")]
    public AudioClip arcDrawingClip;

    [Header("Texture Alignment")]
    [Tooltip("Degrees to rotate longitude so the texture's visible meridians match code coordinates. " +
             "Positive shifts the texture east. Tune until a known location (e.g. LA) lines up.")]
    public float textureLongitudeOffset = 0f;

    [Tooltip("ON: treat the globe texture as a Web-Mercator-style image vertically stretched to " +
             "fill the UV range, and apply a Mercator inversion when converting between lat/lng " +
             "and sphere position.  This is the correct mode for our converted world map (and " +
             "any image where polar regions look pulled-out / mid-latitudes look pushed-out).\n\n" +
             "OFF: fall back to a single linear scale (textureLatitudeScale).  Only correct if " +
             "the texture is a true ±90° equirectangular image — a single linear factor cannot " +
             "fit Mercator distortion at multiple latitudes.")]
    public bool  textureIsMercator      = true;

    [Tooltip("When textureIsMercator is on: the latitude (degrees) at which the texture's top/bottom " +
             "edge sits.  i.e. content above this lat is clipped / smeared.  Smaller value = more " +
             "compression of polar regions on the texture.  Default 66° fits our current map; " +
             "a true ±85.05° Web Mercator texture would use 85.05°.")]
    public float textureMercatorBound   = 66f;

    [Tooltip("Used only when textureIsMercator is OFF.  Linear vertical scale: sphereLat = lat * " +
             "this.  1.0 = true equirectangular.  Cannot fit Mercator distortion at multiple " +
             "latitudes (a single value will only line up at one reference latitude).")]
    public float textureLatitudeScale = 1f;

    public bool HasMarker { get; private set; }

    // True while the post-confirm reveal animation is running.  All user input
    // (spin/zoom/grab/momentum/marker-placement) is blocked during this window
    // so the scripted animation can't be fought by stick or grip input.
    public bool IsAnimatingResults { get; private set; }

    private GameObject        activeMarker;
    private GameObject        correctMarker;
    private List<GameObject>  arcDashes = new List<GameObject>();

    // Pins + arc dashes for previously-completed rounds, populated only at
    // the end of a game (see ShowHistoricalRounds).  Kept in their own list
    // so the per-round activeMarker/correctMarker/arcDashes references are
    // never confused with the historical set.  Cleared by ResetGlobe.
    private List<GameObject>  historicalRoundObjects = new List<GameObject>();

    private AudioSource        audioSource;
    private XRRayInteractor   rayInteractor;

    // Cached at startup BEFORE any pin can become a child of the globe.
    // Placement compares hits to this exact collider so we never accept hits
    // on previously-placed pins (which sit on globe-child transforms and
    // would otherwise pass an IsChildOf check, causing the new pin to float
    // off the surface — the visible bug at low zoom levels where the pin
    // takes up a large fraction of the globe's apparent size).
    private SphereCollider    globeCollider;

    // Tracked as explicit angles so Z never drifts and pitch can be clamped
    private float currentYaw;
    private float currentPitch;

    private InputAction rightStickAction;
    private InputAction leftStickAction;
    private InputAction rightTriggerAction;
    private InputAction leftGripAction;
    private InputAction rightGripAction;
    private bool        triggerWasPressed;

    // Grab-spin / pinch-zoom state — see HandleGrab.
    private Transform leftControllerTransform;
    private Transform rightControllerTransform;
    private bool      wasLeftGripping;
    private bool      wasRightGripping;
    private Vector3   lastLeftGripPos;
    private Vector3   lastRightGripPos;
    private float     lastDualGripDistance;

    // Momentum state — populated at the moment of grip release, decays over
    // time in HandleSpinMomentum.  liveYaw/PitchSpeed are the smoothed live
    // velocity tracked WHILE gripping (deg/sec); when the user releases we
    // copy them into momentumYaw/PitchSpeed and let HandleSpinMomentum coast.
    private float liveYawSpeed;
    private float livePitchSpeed;
    private float momentumYawSpeed;
    private float momentumPitchSpeed;

    // -----------------------------------------------------------------------
    // Unity Lifecycle
    // -----------------------------------------------------------------------

    void OnEnable()
    {
        // Grab or add an AudioSource for globe sounds (marker placement + arc drawing).
        audioSource = GetComponent<AudioSource>();
        if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;

        rayInteractor = FindRightHandRayInteractor();

        // Cache the globe's SphereCollider while no markers exist yet.
        // GetComponentInChildren walks the hierarchy in order, but pin prefabs
        // (sphere head + cylinder) often include their own SphereCollider —
        // and once those are parented to the globe they become children too.
        // Grabbing it here, before ResetGlobe / PlaceMarker can run, locks in
        // the right reference for the rest of the lifetime of this component.
        if (globeCollider == null)
            globeCollider = GetComponentInChildren<SphereCollider>();

        rightStickAction   = new InputAction("RightStick",   binding: "<XRController>{RightHand}/{Primary2DAxis}");
        leftStickAction    = new InputAction("LeftStick",    binding: "<XRController>{LeftHand}/{Primary2DAxis}");
        rightTriggerAction = new InputAction("RightTrigger", binding: "<XRController>{RightHand}/{TriggerButton}");
        leftGripAction     = new InputAction("LeftGrip",     binding: "<XRController>{LeftHand}/{GripButton}");
        rightGripAction    = new InputAction("RightGrip",    binding: "<XRController>{RightHand}/{GripButton}");

        rightStickAction.Enable();
        leftStickAction.Enable();
        rightTriggerAction.Enable();
        leftGripAction.Enable();
        rightGripAction.Enable();
    }

    XRRayInteractor FindRightHandRayInteractor()
    {
        // Include inactive — XRI sometimes hot-swaps ray/near interactors,
        // so the right-hand one may be disabled at the moment we search.
        var allInteractors = FindObjectsByType<XRRayInteractor>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        // 1. Look for one under a "Right Controller" parent
        foreach (var interactor in allInteractors)
        {
            Transform t = interactor.transform;
            while (t != null)
            {
                if (t.name.ToLower().Contains("right controller")) return interactor;
                t = t.parent;
            }
        }

        // 2. Explicit handedness check
        foreach (var interactor in allInteractors)
        {
            if (interactor.handedness == UnityEngine.XR.Interaction.Toolkit.Interactors.InteractorHandedness.Right)
                return interactor;
        }

        // 3. Fall back to any NON-gaze interactor
        foreach (var interactor in allInteractors)
        {
            Transform t = interactor.transform;
            bool isGaze = false;
            while (t != null)
            {
                if (t.name.ToLower().Contains("gaze")) { isGaze = true; break; }
                t = t.parent;
            }
            if (!isGaze) return interactor;
        }

        // Don't warn here — Update() will retry each frame until one appears.
        return null;
    }

    void OnDisable()
    {
        rightStickAction?.Disable();
        leftStickAction?.Disable();
        rightTriggerAction?.Disable();
        leftGripAction?.Disable();
        rightGripAction?.Disable();
    }

    void Update()
    {
        // Lazy-find: XRI may spawn/enable the right-hand ray interactor after
        // our OnEnable runs (especially the first time guess mode opens).
        if (rayInteractor == null) rayInteractor = FindRightHandRayInteractor();

        // While the post-confirm reveal animation is running, ALL user input
        // is suppressed — the scripted animation owns the globe's rotation and
        // scale, and any concurrent stick / grip input would fight it.
        if (IsAnimatingResults) return;

        // CRITICAL: marker placement MUST run before anything that mutates
        // transform.localRotation this frame.  TryGetCurrent3DRaycastHit
        // returns the hit cached against the globe's CURRENT orientation —
        // if HandleSpin / HandleGrab / HandleSpinMomentum have already
        // rotated the globe this frame, then converting that world hit to
        // local space via InverseTransformPoint uses the new (mismatched)
        // rotation and the marker lands at the wrong lat/lng.  Especially
        // visible when momentum is coasting after a hard grab-spin.
        if (rayInteractor != null) HandleMarkerPlacement();

        HandleSpin();
        HandleZoom();
        HandleGrab();
        HandleSpinMomentum();
    }

    // -----------------------------------------------------------------------
    // Spin — right thumbstick rotates the globe
    // Yaw and pitch are tracked separately so Z never drifts.
    // Pitch is clamped to ±80° so the globe can never flip upside down.
    // -----------------------------------------------------------------------

    void HandleSpin()
    {
        Vector2 axis = rightStickAction.ReadValue<Vector2>();
        if (axis.magnitude < 0.1f) return;

        // Joystick input takes over from any in-progress momentum coast.
        // Without this, holding the stick after a hard grab-spin would feel
        // like fighting the globe.
        momentumYawSpeed   = 0f;
        momentumPitchSpeed = 0f;

        currentYaw   += axis.x * spinSensitivity * Time.deltaTime;
        currentPitch  = Mathf.Clamp(
            currentPitch - axis.y * spinSensitivity * Time.deltaTime,
            -80f, 80f);

        // Apply yaw first (around world Y), then pitch (around world X).
        // This keeps the equator horizontal — pitch tilts toward/away from viewer,
        // yaw spins like Earth. Using AngleAxis with world axes prevents the
        // local-axis drift that Quaternion.Euler causes when combining the two.
        transform.localRotation =
            Quaternion.AngleAxis(currentPitch, Vector3.right) *
            Quaternion.AngleAxis(currentYaw,   Vector3.up);
    }

    // -----------------------------------------------------------------------
    // Zoom — left thumbstick Y scales the globe
    // -----------------------------------------------------------------------

    // Whichever transform the zoom slider scales — pivot if set, else self.
    // Centralized here so HandleZoom, ResetGlobe, and SetDisplayScale all agree.
    Transform ScaleTarget => zoomPivot != null ? zoomPivot : transform;

    void HandleZoom()
    {
        Vector2 axis   = leftStickAction.ReadValue<Vector2>();
        float   scroll = axis.y;

        if (Mathf.Abs(scroll) > 0.1f)
        {
            Transform t = ScaleTarget;
            float newScale = Mathf.Clamp(
                t.localScale.x + scroll * zoomSpeed * Time.deltaTime,
                minScale, maxScale);
            t.localScale = Vector3.one * newScale;
        }
    }

    // Public hook for GuessManager to lock the globe at a fixed display size
    // during the results UI.  Routes through ScaleTarget so the pivot is used
    // when configured, keeping the back-of-globe anchor consistent.
    public void SetDisplayScale(float scale)
    {
        ScaleTarget.localScale = Vector3.one * scale;
    }

    // -----------------------------------------------------------------------
    // Grab-and-spin / two-handed pinch-zoom
    //
    // Squeeze ONE controller's grip → drag your hand to spin the globe (the
    // surface "sticks" to your hand).  Squeeze BOTH grips → pull your hands
    // apart to zoom in, push them together to zoom out.  Both work in addition
    // to the joystick spin/zoom — they don't replace it.
    //
    // Sensitivity is exposed in the inspector (grabSpinSensitivity,
    // grabZoomSensitivity) so the feel can be tuned without code changes.
    // -----------------------------------------------------------------------

    void HandleGrab()
    {
        if (leftGripAction == null || rightGripAction == null) return;

        // Lazy-find controller transforms — the XR rig may not be fully spawned
        // when OnEnable runs (especially the first time guess mode opens).
        if (leftControllerTransform  == null) leftControllerTransform  = FindControllerTransform("left controller");
        if (rightControllerTransform == null) rightControllerTransform = FindControllerTransform("right controller");

        bool L = leftGripAction.ReadValue<float>()  >= gripActivationThreshold && leftControllerTransform  != null;
        bool R = rightGripAction.ReadValue<float>() >= gripActivationThreshold && rightControllerTransform != null;

        // Any fresh grab kills an in-progress momentum coast — the user is
        // taking manual control again.  liveYaw/PitchSpeed are reset too so
        // the next release uses only the velocity from THIS grab gesture,
        // not whatever was lingering from before.
        bool wasGripping    = wasLeftGripping || wasRightGripping;
        bool isGripping     = L || R;
        bool justStartedGrip = isGripping && !wasGripping;
        if (justStartedGrip)
        {
            momentumYawSpeed   = 0f;
            momentumPitchSpeed = 0f;
            liveYawSpeed       = 0f;
            livePitchSpeed     = 0f;
        }

        if (L && R)
        {
            // ── Dual-grip pinch-zoom ────────────────────────────────────────
            Vector3 lp   = leftControllerTransform.position;
            Vector3 rp   = rightControllerTransform.position;
            float   dist = Vector3.Distance(lp, rp);

            // First frame in dual mode (entered from single or no-grip): just
            // record the baseline distance — DON'T scale yet, otherwise we'd
            // get a one-frame jump from whatever the previous baseline was.
            if (!wasLeftGripping || !wasRightGripping)
            {
                lastDualGripDistance = dist;
                // Switching from spin → zoom: zero the live spin velocity so
                // releasing both grips later doesn't fling the globe with
                // velocity that was being tracked before the second hand came in.
                liveYawSpeed   = 0f;
                livePitchSpeed = 0f;
            }
            else
            {
                float deltaMeters = dist - lastDualGripDistance;
                Transform t = ScaleTarget;
                float newScale = Mathf.Clamp(
                    t.localScale.x + deltaMeters * grabZoomSensitivity,
                    minScale, maxScale);
                t.localScale = Vector3.one * newScale;
                lastDualGripDistance = dist;
            }
        }
        else if (L)
        {
            // ── Single-grip spin (left hand) ────────────────────────────────
            Vector3 lp = leftControllerTransform.position;
            // Reset baseline whenever the active hand just changed (was no-grip,
            // was right-only, or was dual) so there's no positional jump.
            if (!wasLeftGripping || wasRightGripping)
                lastLeftGripPos = lp;
            else
                ApplyGrabSpin(lp - lastLeftGripPos);
            lastLeftGripPos = lp;
        }
        else if (R)
        {
            // ── Single-grip spin (right hand) ───────────────────────────────
            Vector3 rp = rightControllerTransform.position;
            if (!wasRightGripping || wasLeftGripping)
                lastRightGripPos = rp;
            else
                ApplyGrabSpin(rp - lastRightGripPos);
            lastRightGripPos = rp;
        }

        // Release detection — if the user was gripping last frame and isn't this
        // frame, copy the smoothed live velocity into the momentum state so the
        // globe keeps spinning for a while like a real globe.
        if (wasGripping && !isGripping)
        {
            momentumYawSpeed   = liveYawSpeed;
            momentumPitchSpeed = livePitchSpeed;
            liveYawSpeed       = 0f;
            livePitchSpeed     = 0f;
        }

        wasLeftGripping  = L;
        wasRightGripping = R;
    }

    // Convert a world-space hand-motion delta into yaw/pitch on the globe.
    // Project the delta onto the user's view-relative right and up axes so
    // "drag right" always means "rotate right from the user's perspective"
    // regardless of which way the user is facing in the room.
    //
    // Side effect: also updates the smoothed liveYaw/PitchSpeed (deg/sec) used
    // as the release velocity for momentum coasting.  Smoothing prevents one
    // stray jitter frame from launching the globe at the wrong speed.
    void ApplyGrabSpin(Vector3 deltaWorld)
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        float dx = Vector3.Dot(deltaWorld, cam.transform.right);
        float dy = Vector3.Dot(deltaWorld, cam.transform.up);

        if (invertGrabSpinX) dx = -dx;
        if (invertGrabSpinY) dy = -dy;

        // Sign convention chosen so the globe surface "follows" the hand:
        //   drag hand right (+dx) → globe front rotates right → currentYaw decreases
        //   drag hand up    (+dy) → globe top tips toward viewer → currentPitch increases
        // (Inverse of the joystick convention, which is intentional — the joystick
        // pans the camera-view across the globe, the hand drags the globe itself.)
        float yawDelta   = -dx * grabSpinSensitivity;
        float pitchDelta =  dy * grabSpinSensitivity;

        // Pitch clamp: if we hit the limit, the EFFECTIVE pitch delta this frame is
        // smaller than what the hand asked for.  Use the actual applied delta to
        // feed the velocity tracker so momentum can't push past the clamp on release.
        float beforePitch = currentPitch;
        currentYaw   += yawDelta;
        currentPitch  = Mathf.Clamp(currentPitch + pitchDelta, -80f, 80f);
        float appliedPitchDelta = currentPitch - beforePitch;

        transform.localRotation =
            Quaternion.AngleAxis(currentPitch, Vector3.right) *
            Quaternion.AngleAxis(currentYaw,   Vector3.up);

        // Track live angular velocity (deg/sec), exponentially smoothed.  The
        // time-constant `grabVelocitySmoothing` controls how reactive vs. how
        // averaged this is — small values feel snappy, large values forgive
        // a fumbled final frame.
        if (Time.deltaTime > 0f)
        {
            float instantYaw   = yawDelta          / Time.deltaTime;
            float instantPitch = appliedPitchDelta / Time.deltaTime;
            float t = grabVelocitySmoothing > 0f
                ? 1f - Mathf.Exp(-Time.deltaTime / grabVelocitySmoothing)
                : 1f;
            liveYawSpeed   = Mathf.Lerp(liveYawSpeed,   instantYaw,   t);
            livePitchSpeed = Mathf.Lerp(livePitchSpeed, instantPitch, t);
        }
    }

    // Apply the post-release coast.  Decays exponentially per grabMomentumFriction;
    // snaps to zero once both axes drop below grabMomentumMinSpeed so the globe
    // actually stops instead of drifting imperceptibly forever.
    //
    // Skipped while gripping (HandleGrab is driving rotation directly) and while
    // the joystick is engaged (HandleSpin already cleared the momentum to zero).
    void HandleSpinMomentum()
    {
        // Don't coast on top of an active grab.
        if (wasLeftGripping || wasRightGripping) return;

        if (Mathf.Abs(momentumYawSpeed)   < grabMomentumMinSpeed &&
            Mathf.Abs(momentumPitchSpeed) < grabMomentumMinSpeed)
        {
            momentumYawSpeed   = 0f;
            momentumPitchSpeed = 0f;
            return;
        }

        currentYaw   += momentumYawSpeed   * Time.deltaTime;
        currentPitch  = Mathf.Clamp(currentPitch + momentumPitchSpeed * Time.deltaTime, -80f, 80f);

        // Hitting the pitch clamp should kill the pitch component of momentum so
        // the globe doesn't keep "pushing" against the rail.
        if (currentPitch <= -80f || currentPitch >= 80f)
            momentumPitchSpeed = 0f;

        transform.localRotation =
            Quaternion.AngleAxis(currentPitch, Vector3.right) *
            Quaternion.AngleAxis(currentYaw,   Vector3.up);

        float decay = Mathf.Exp(-grabMomentumFriction * Time.deltaTime);
        momentumYawSpeed   *= decay;
        momentumPitchSpeed *= decay;
    }

    // Find a controller GameObject by partial name match (case-insensitive).
    // Mirrors FindRightHandRayInteractor's name-walk strategy so naming stays
    // consistent across the project.  Cached after the first hit by HandleGrab.
    Transform FindControllerTransform(string nameContains)
    {
        nameContains = nameContains.ToLower();
        var all = FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (var t in all)
            if (t.name.ToLower().Contains(nameContains)) return t;
        return null;
    }

    // -----------------------------------------------------------------------
    // Marker Placement — right trigger tap while ray points at globe
    // -----------------------------------------------------------------------

    void HandleMarkerPlacement()
    {
        bool triggerPressed = rightTriggerAction.ReadValue<float>() > 0.5f;

        if (triggerPressed && !triggerWasPressed)
        {
            // Use rayOriginTransform (XRI's internal ray child) so our Physics.Raycast
            // originates from the exact same point/direction as the visible ray line.
            // Falls back to the interactor's own transform if rayOriginTransform is null.
            Transform origin = rayInteractor.rayOriginTransform != null
                ? rayInteractor.rayOriginTransform
                : rayInteractor.transform;

            Ray ray = new Ray(origin.position, origin.forward);

            if (Physics.Raycast(ray, out RaycastHit hit)
                && hit.collider != null
                && hit.collider.transform.IsChildOf(transform))
            {
                PlaceMarker(hit.point);
            }
        }

        triggerWasPressed = triggerPressed;
    }

    void SetLayerRecursively(GameObject obj, int layer)
    {
        obj.layer = layer;
        foreach (Transform child in obj.transform)
            SetLayerRecursively(child.gameObject, layer);
    }  
    // -----------------------------------------------------------------------
    // Marker Placement
    // -----------------------------------------------------------------------

    void PlaceMarker(Vector3 worldHitPoint)
    {
        if (activeMarker != null)
            Destroy(activeMarker);

        // Work in globe-local space, identical to PlaceCorrectMarker, so the two
        // pins always render at the same world size regardless of globe zoom or
        // parent scale.  Using SetParent(..., true) on one pin and (..., false)
        // on the other causes a scale mismatch whenever the globe isn't at 1.0.
        //
        // CRITICAL: use `localPoint` directly — NOT `localDir * col.radius`.
        // SphereCollider.radius is in the COLLIDER'S transform frame (often a
        // child of the globe with its own local scale/offset), so multiplying
        // it back as a parent-local distance lands the pin off the surface.
        // The hit point already IS on the surface in world space; converting
        // it to local space gives the exact correct local position.
        Vector3 localPoint = transform.InverseTransformPoint(worldHitPoint);
        Vector3 localDir   = localPoint.normalized;

        // Diagnostic — surface alignment between collider and visual mesh.
        // If colliderWorldCentre ≠ transform.position the collider is offset
        // (non-zero `center` field or sits on an offset child); placement is
        // correct against the collider but visibly off the mesh.
        // If colliderWorldRadius doesn't match (worldHit − colliderWorldCentre)
        // length, the hit isn't on this collider at all.
        float expectedLocalR = GetLocalSurfaceRadius();
        Vector3 colCentre = globeCollider != null ? globeCollider.bounds.center : transform.position;
        float   colWorldR = globeCollider != null ? globeCollider.bounds.extents.x : 0f;
        float   hitToColCentre = (worldHitPoint - colCentre).magnitude;
        Debug.Log($"[GlobeInteractable.PlaceMarker] " +
                  $"worldHit={worldHitPoint} " +
                  $"globeXform={transform.position} " +
                  $"colliderCentre={colCentre} " +
                  $"colliderWorldR={colWorldR:F4} " +
                  $"hitToColliderCentre={hitToColCentre:F4} " +
                  $"|localPoint|={localPoint.magnitude:F4} " +
                  $"expectedLocalR={expectedLocalR:F4} " +
                  $"lossy={transform.lossyScale.x:F4} " +
                  $"colliderName={(globeCollider != null ? globeCollider.name : "null")} " +
                  $"colliderGO={(globeCollider != null ? globeCollider.gameObject.name : "null")}");

        
        activeMarker = Instantiate(markerPrefab);
        activeMarker.transform.SetParent(transform, false);  // keep local scale
        SetLayerRecursively(activeMarker, LayerMask.NameToLayer("Ignore Raycast"));
        activeMarker.transform.localPosition = localPoint;
        // Align pin's local +Y (sphere head) with the outward surface normal —
        // the cylinder then points inward into the globe.
        activeMarker.transform.localRotation = Quaternion.FromToRotation(Vector3.up, localDir);

        // The prefab's pivot may not actually sit at the cylinder's tip — depending
        // on how the prefab was authored, the pivot could be at the sphere center,
        // mesh centroid, etc.  Shift the pin outward along the surface normal so
        // its lowest point along that normal lands exactly on the hit surface.
        // Result: the BASE of the pin is at the user's pick, regardless of pivot.
        SeatPinOnSurface(activeMarker, worldHitPoint);

        HasMarker = true;

        if (audioSource != null && markerPlacedClip != null)
            audioSource.PlayOneShot(markerPlacedClip);
    }

    // Pushes the pin outward along the surface normal so the lowest point of its
    // bounds (in the outward-normal axis) sits flush at worldHitPoint.  Uses
    // renderer bounds rather than the prefab's pivot so that pins authored with
    // arbitrary pivots (sphere center, mesh centroid, etc.) all plant correctly.
    void SeatPinOnSurface(GameObject pin, Vector3 worldHitPoint)
    {
        var rends = pin.GetComponentsInChildren<Renderer>();
        if (rends.Length == 0) return;

        // Combine all child renderer bounds into one world-space AABB.
        Bounds wb = rends[0].bounds;
        for (int i = 1; i < rends.Length; i++) wb.Encapsulate(rends[i].bounds);

        Vector3 outward = (worldHitPoint - transform.position).normalized;
        if (outward.sqrMagnitude < 0.0001f) return;

        // Find the bounds corner with the smallest projection along `outward`
        // relative to the hit point.  If that projection is negative, the pin
        // pokes past the hit point into the globe — shift it outward by that amount.
        Vector3 mn = wb.min, mx = wb.max;
        float minProj = float.PositiveInfinity;
        for (int i = 0; i < 8; i++)
        {
            Vector3 c = new Vector3(
                (i & 1) == 0 ? mn.x : mx.x,
                (i & 2) == 0 ? mn.y : mx.y,
                (i & 4) == 0 ? mn.z : mx.z);
            float p = Vector3.Dot(c - worldHitPoint, outward);
            if (p < minProj) minProj = p;
        }

        if (minProj < 0f)
            pin.transform.position += outward * (-minProj);
    }

    // Finds the globe's actual world-space radius by scanning its sphere collider.
    // Works whether the collider is on this GameObject or a child (e.g. "Globe Sphere").
    float GetGlobeWorldRadius()
    {
        var col = GetComponentInChildren<SphereCollider>();
        if (col != null) return col.bounds.extents.x;
        return transform.localScale.x * 0.5f;
    }

    // Surface radius expressed in THIS transform's local space.  Derived from
    // the collider's world bounds (which include all parent/child scales) and
    // divided back by the globe's own lossyScale, so the result is in units
    // that match `transform.InverseTransformPoint(worldSurfacePoint).magnitude`.
    //
    // This is what callers should multiply a unit local-space direction by to
    // land on the globe surface.  Using SphereCollider.radius directly is wrong
    // when the collider sits on a child with a different scale than the globe
    // root — the chief cause of "pin floats away from the surface" bugs.
    float GetLocalSurfaceRadius()
    {
        var col = GetComponentInChildren<SphereCollider>();
        if (col == null) return 0.5f;
        float worldR = col.bounds.extents.x;
        float scale  = transform.lossyScale.x;
        return scale > 0.0001f ? worldR / scale : 0.5f;
    }

    // -----------------------------------------------------------------------
    // Lat / Lng Conversion — pure equirectangular math.
    // The globe texture MUST be a proper ±90° equirectangular image for this
    // to be accurate (see MercatorToEquirectangular.cs).
    // -----------------------------------------------------------------------

    public Vector2 GetGuessLatLng()
    {
        if (!HasMarker) return Vector2.zero;

        Vector3 local = transform.InverseTransformPoint(activeMarker.transform.position).normalized;

        // Geometric sphere latitude — the angle implied by the 3D hit point.
        float sphereLat = Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg;

        // Convert sphere latitude to TEXTURE latitude using whichever projection
        // the texture actually represents.  See TextureLatFromSphereLat for the math.
        float lat = TextureLatFromSphereLat(sphereLat);

        // Unity's built-in sphere UV wraps OPPOSITE to standard longitude convention:
        // going east (+lng) on the texture corresponds to going -x on the sphere.
        // Negating local.z in the atan2 flips the reference axis and gives a raw
        // angle that increases with longitude — so `lng = rawLng - offset` works
        // cleanly, with offset ≈ 90° (calibrated from three equator landmarks).
        float rawLng = Mathf.Atan2(local.x, -local.z) * Mathf.Rad2Deg;
        float lng    = rawLng - textureLongitudeOffset;

        // Wrap into [-180, 180]
        if (lng > 180f)  lng -= 360f;
        if (lng < -180f) lng += 360f;

        Debug.Log($"[Globe Debug] local=({local.x:F3},{local.y:F3},{local.z:F3}) | sphereLat={sphereLat:F1}° | lat={lat:F1}° | rawLng={rawLng:F1}° | offset={textureLongitudeOffset}° | merc={textureIsMercator} bound={textureMercatorBound}° latScale={textureLatitudeScale} | lng={lng:F1}°");

        return new Vector2(lat, lng);
    }

    // -----------------------------------------------------------------------
    // Correct Location Marker — placed by GuessManager after confirming
    // -----------------------------------------------------------------------

    public void PlaceCorrectMarker(float lat, float lng)
    {
        if (correctMarkerPrefab == null)
        {
            Debug.LogWarning("GlobeInteractable: correctMarkerPrefab is not assigned — skipping green marker.");
            return;
        }

        if (correctMarker != null)
            Destroy(correctMarker);

        // Convert TEXTURE latitude to SPHERE latitude using whichever projection
        // the texture actually represents.  Must match the inverse transform in
        // GetGuessLatLng so guess/correct markers align pixel-for-pixel.
        float sphereLatRad = SphereLatFromTextureLat(lat) * Mathf.Deg2Rad;
        float lngRad       = (lng + textureLongitudeOffset) * Mathf.Deg2Rad;

        // Mirror the z component to match Unity's flipped sphere UV direction
        // (see GetGuessLatLng for why).  Without this, localDir lands on the
        // antipode of where the texture actually shows that (lat, lng).
        Vector3 localDir = new Vector3(
            Mathf.Cos(sphereLatRad) * Mathf.Sin(lngRad),
            Mathf.Sin(sphereLatRad),
           -Mathf.Cos(sphereLatRad) * Mathf.Cos(lngRad)
        ).normalized;

        // Round-trip check: back out lat/lng from localDir — should match inputs exactly.
        float sphereLatCheck = Mathf.Asin(Mathf.Clamp(localDir.y, -1f, 1f)) * Mathf.Rad2Deg;
        float latCheck       = TextureLatFromSphereLat(sphereLatCheck);
        float lngCheck       = Mathf.Atan2(localDir.x, -localDir.z) * Mathf.Rad2Deg - textureLongitudeOffset;

        Debug.Log($"[CorrectMarker Debug] input lat={lat:F1}° lng={lng:F1}° | sphereLat={sphereLatCheck:F1}° | merc={textureIsMercator} bound={textureMercatorBound}° | localDir=({localDir.x:F3},{localDir.y:F3},{localDir.z:F3}) | reverse lat={latCheck:F1}° lng={lngCheck:F1}°");

        // Place in globe-local space so parent rotation/scale can't shift it.
        // Sit the pin's pivot right on the sphere surface (no outward padding) —
        // with the cylinder pointing inward, its stem now embeds into the globe
        // and the sphere head pokes outward, exactly like a real map pin.
        // GetLocalSurfaceRadius is collider-on-child-safe (see its comment).
        float localRadius = GetLocalSurfaceRadius();

        correctMarker = Instantiate(correctMarkerPrefab);
        correctMarker.transform.SetParent(transform, false);  // don't preserve world pos
        correctMarker.transform.localPosition = localDir * localRadius;

        // Align the pin's local +Y (sphere head) with the outward surface normal.
        // The cylinder, running from the sphere toward the prefab's -Y, therefore
        // points into the globe.
        correctMarker.transform.localRotation = Quaternion.FromToRotation(Vector3.up, localDir);

        // Seat the green pin the same way the red pin is seated (see PlaceMarker)
        // so both pins plant at their bases regardless of prefab pivot.  Compute
        // the world-space "hit" point as the surface point at this lat/lng.
        Vector3 surfaceWorld = transform.TransformPoint(localDir * localRadius);
        SeatPinOnSurface(correctMarker, surfaceWorld);
    }

    // -----------------------------------------------------------------------
    // Lat/Lng → globe-local direction.  Mirrors the math in PlaceCorrectMarker
    // so animation code can target the same on-sphere point that the green
    // pin will eventually be planted at.
    // -----------------------------------------------------------------------
    Vector3 LatLngToLocalDir(float lat, float lng)
    {
        float sphereLatRad = SphereLatFromTextureLat(lat) * Mathf.Deg2Rad;
        float lngRad       = (lng + textureLongitudeOffset) * Mathf.Deg2Rad;
        return new Vector3(
            Mathf.Cos(sphereLatRad) * Mathf.Sin(lngRad),
            Mathf.Sin(sphereLatRad),
           -Mathf.Cos(sphereLatRad) * Mathf.Cos(lngRad)
        ).normalized;
    }

    // -----------------------------------------------------------------------
    // Texture latitude ↔ sphere latitude.  Two modes, selected by
    // `textureIsMercator`:
    //
    //   Mercator (default).  Treats the texture as a Web-Mercator-style image
    //   stretched to fill the UV range, with its top/bottom edges sitting at
    //   ±textureMercatorBound° lat.  This matches our world map texture, where
    //   a single linear scale couldn't simultaneously align points at multiple
    //   latitudes (Paris and Seoul required different scale factors).
    //
    //   Forward (texture→sphere):
    //     sphereLat = 90 * ln(tan(π/4 + lat/2)) / ln(tan(π/4 + bound/2))
    //   Inverse (sphere→texture):
    //     lat = 2 * atan(exp(sphereLat * K / 90)) - π/2,  K = ln(tan(π/4 + bound/2))
    //
    //   Linear.  sphereLat = lat * textureLatitudeScale.  Only correct for true
    //   ±90° equirectangular textures (textureLatitudeScale = 1).
    //
    // Both directions must stay perfect inverses or the green correct-marker
    // and the red guess-marker will diverge over a round-trip.
    // -----------------------------------------------------------------------
    float SphereLatFromTextureLat(float texLatDeg)
    {
        if (!textureIsMercator)
            return texLatDeg * textureLatitudeScale;

        float bound = Mathf.Clamp(textureMercatorBound, 1f, 89.5f);
        float K     = Mathf.Log(Mathf.Tan(Mathf.PI / 4f + bound  * 0.5f * Mathf.Deg2Rad));
        // Clamp lat away from the poles so tan() doesn't blow up.
        float lat   = Mathf.Clamp(texLatDeg, -89.9f, 89.9f);
        float merc  = Mathf.Log(Mathf.Tan(Mathf.PI / 4f + lat   * 0.5f * Mathf.Deg2Rad));
        return 90f * merc / K;
    }

    float TextureLatFromSphereLat(float sphereLatDeg)
    {
        if (!textureIsMercator)
            return sphereLatDeg / Mathf.Max(0.001f, textureLatitudeScale);

        float bound = Mathf.Clamp(textureMercatorBound, 1f, 89.5f);
        float K     = Mathf.Log(Mathf.Tan(Mathf.PI / 4f + bound * 0.5f * Mathf.Deg2Rad));
        // Clamp the exponent argument so exp() doesn't overflow at the poles.
        float arg   = Mathf.Clamp(sphereLatDeg * K / 90f, -10f, 10f);
        return (2f * Mathf.Atan(Mathf.Exp(arg)) - Mathf.PI / 2f) * Mathf.Rad2Deg;
    }

    // Compute (yaw, pitch) — using this script's
    //   R = AngleAxis(pitch, X) * AngleAxis(yaw, Y)
    // convention — that brings a globe-local point M to the parent-local
    // "front" direction (0, 0, -1), i.e. centers it in the user's view.
    //
    // Derivation: AngleAxis(yaw, Y) sends M to v = (cos·Mx + sin·Mz, My, -sin·Mx + cos·Mz);
    // pick yaw so v.x = 0 and v.z ≤ 0 → yaw = atan2(Mx, -Mz).  Then AngleAxis(pitch, X)
    // takes v to (0, 0, -1) → pitch = atan2(-My, sqrt(Mx² + Mz²)).
    void ComputeCenterYawPitch(Vector3 M_local, out float yawDeg, out float pitchDeg)
    {
        Vector3 m = M_local.normalized;
        float S = Mathf.Sqrt(m.x * m.x + m.z * m.z);
        yawDeg   = Mathf.Atan2(m.x, -m.z) * Mathf.Rad2Deg;
        pitchDeg = Mathf.Atan2(-m.y, S)   * Mathf.Rad2Deg;
        pitchDeg = Mathf.Clamp(pitchDeg, -80f, 80f);
    }

    void ApplyYawPitch()
    {
        transform.localRotation =
            Quaternion.AngleAxis(currentPitch, Vector3.right) *
            Quaternion.AngleAxis(currentYaw,   Vector3.up);
    }

    // -----------------------------------------------------------------------
    // Results Reveal Animation
    //
    // Phases:
    //   1. zoom-in to zoomedInScale + spin so the guess marker is centered
    //   2. pause
    //   3. spin to the correct location while progressively drawing a
    //      dotted great-circle arc between the two points
    //   4. plant the green correct-marker
    //   5. pause
    //   6. shrink to endScale (the results-panel display size)
    //
    // User input is suppressed throughout via IsAnimatingResults (gated in
    // Update), so stick / grip input can't fight the scripted animation.
    // -----------------------------------------------------------------------
    public IEnumerator AnimateResultsReveal(float correctLat, float correctLng, float endScale)
    {
        if (!HasMarker || activeMarker == null)
        {
            Debug.LogWarning("GlobeInteractable.AnimateResultsReveal: no guess marker — aborting animation.");
            yield break;
        }

        IsAnimatingResults = true;

        // Kill any leftover input-driven motion so it can't bleed into the
        // first animated frame.
        liveYawSpeed = livePitchSpeed = 0f;
        momentumYawSpeed = momentumPitchSpeed = 0f;

        // Globe-local directions for the two endpoints of the reveal.  Reading
        // the guess from the live marker (rather than reverse-mapping its
        // lat/lng) is more accurate — it's exactly where the pin sits.
        Vector3 guessLocal   = transform.InverseTransformPoint(activeMarker.transform.position).normalized;
        Vector3 correctLocal = LatLngToLocalDir(correctLat, correctLng);

        float startScale = ScaleTarget.localScale.x;
        float startYaw   = currentYaw;
        float startPitch = currentPitch;

        // ── Phase 1: zoom-in + center on the guess marker ──────────────────
        float yawA, pitchA;
        ComputeCenterYawPitch(guessLocal, out yawA, out pitchA);
        // Take the shortest yaw path from the current yaw — without DeltaAngle
        // a 359°→1° transition would spin the whole way around.
        yawA = startYaw + Mathf.DeltaAngle(startYaw, yawA);
        yield return AnimateScaleYawPitch(
            startScale, zoomedInScale,
            startYaw,   yawA,
            startPitch, pitchA,
            zoomToGuessDuration);

        // ── Phase 2: hold on the guess marker ──────────────────────────────
        if (pauseAtGuessDuration > 0f)
            yield return new WaitForSeconds(pauseAtGuessDuration);

        // ── Phase 3: spin from guess → correct, drawing the dotted arc ─────
        float yawB, pitchB;
        ComputeCenterYawPitch(correctLocal, out yawB, out pitchB);
        yawB = yawA + Mathf.DeltaAngle(yawA, yawB);

        // Pre-build the arc in globe-local space.  Because the dots are
        // children of the globe they rotate with it automatically as
        // yaw/pitch animate — no per-frame transform updates needed.
        CreateArcDots(guessLocal, correctLocal);

        // Reveal the dots progressively over the spin so the line "draws"
        // toward the correct location instead of appearing all at once.
        yield return AnimateSpinAndRevealArc(yawA, yawB, pitchA, pitchB, spinToCorrectDuration);

        // ── Phase 4: plant the green marker ────────────────────────────────
        PlaceCorrectMarker(correctLat, correctLng);

        // ── Phase 5: pause to let the player register both pins + the line ─
        if (pauseAtCorrectDuration > 0f)
            yield return new WaitForSeconds(pauseAtCorrectDuration);

        // ── Phase 6: shrink to results-panel size, holding orientation ─────
        yield return AnimateScaleYawPitch(
            zoomedInScale, endScale,
            yawB, yawB,
            pitchB, pitchB,
            shrinkToResultsDuration);

        IsAnimatingResults = false;
    }

    IEnumerator AnimateScaleYawPitch(float scaleFrom, float scaleTo,
                                     float yawFrom,   float yawTo,
                                     float pitchFrom, float pitchTo,
                                     float duration)
    {
        if (duration <= 0f)
        {
            ScaleTarget.localScale = Vector3.one * scaleTo;
            currentYaw   = yawTo;
            currentPitch = pitchTo;
            ApplyYawPitch();
            yield break;
        }

        float t0 = Time.time;
        while (true)
        {
            float u = Mathf.Clamp01((Time.time - t0) / duration);
            float s = Mathf.SmoothStep(0f, 1f, u);   // ease-in/out
            ScaleTarget.localScale = Vector3.one * Mathf.Lerp(scaleFrom, scaleTo, s);
            currentYaw   = Mathf.Lerp(yawFrom,   yawTo,   s);
            currentPitch = Mathf.Lerp(pitchFrom, pitchTo, s);
            ApplyYawPitch();
            if (u >= 1f) yield break;
            yield return null;
        }
    }

    // Phase-3 specialization: tween yaw/pitch and progressively reveal dashes.
    IEnumerator AnimateSpinAndRevealArc(float yawFrom,   float yawTo,
                                        float pitchFrom, float pitchTo,
                                        float duration)
    {
        // Start with every dash hidden.
        for (int i = 0; i < arcDashes.Count; i++)
            if (arcDashes[i] != null) arcDashes[i].SetActive(false);

        if (duration <= 0f)
        {
            currentYaw = yawTo; currentPitch = pitchTo; ApplyYawPitch();
            for (int i = 0; i < arcDashes.Count; i++)
                if (arcDashes[i] != null) arcDashes[i].SetActive(true);
            yield break;
        }

        // Loop the arc-drawing sound for the duration of the spin phase.
        if (audioSource != null && arcDrawingClip != null)
        {
            audioSource.clip   = arcDrawingClip;
            audioSource.loop   = true;
            audioSource.Play();
        }

        float t0 = Time.time;
        while (true)
        {
            float u = Mathf.Clamp01((Time.time - t0) / duration);
            float s = Mathf.SmoothStep(0f, 1f, u);
            currentYaw   = Mathf.Lerp(yawFrom,   yawTo,   s);
            currentPitch = Mathf.Lerp(pitchFrom, pitchTo, s);
            ApplyYawPitch();

            // Reveal dashes up to current progress.
            int reveal = Mathf.CeilToInt(s * arcDashes.Count);
            for (int i = 0; i < arcDashes.Count; i++)
                if (arcDashes[i] != null) arcDashes[i].SetActive(i < reveal);

            if (u >= 1f) break;
            yield return null;
        }

        // Arc finished — stop the looping sound.
        if (audioSource != null) audioSource.Stop();
    }

    // -----------------------------------------------------------------------
    // Dashed great-circle arc between two globe-local surface directions.
    //
    // The arc spans an angular range of θ = acos(a · b) along the great circle.
    // We split that into `arcDashCount` equal angular segments and place one
    // dash per segment.  Each dash covers the FRONT `arcDashFill` fraction of
    // its segment, leaving the rest as a gap — that's what makes it look dashed
    // rather than continuous.
    //
    // Dash geometry: an elongated cube oriented with its local +Z along the
    // arc tangent.  Length is the chord between the two angular endpoints of
    // the dash's "filled" portion, and cross-section is arcDashThickness.
    //
    // Dashes are children of the globe so they rotate with it; cleaned up by
    // ResetGlobe / DestroyArcDots.
    // -----------------------------------------------------------------------
    void CreateArcDots(Vector3 fromLocalDir, Vector3 toLocalDir)
    {
        DestroyArcDots();
        BuildArcDashesInto(fromLocalDir, toLocalDir, arcDashes);
    }

    // The actual dash-construction loop, factored out of CreateArcDots so that
    // the end-of-game "show all prior rounds" path can reuse the exact same
    // geometry/material logic without disturbing the active-round arcDashes
    // list.  Appends the created GameObjects to `target` so the caller can
    // own their lifetime.
    void BuildArcDashesInto(Vector3 fromLocalDir, Vector3 toLocalDir, List<GameObject> target)
    {
        if (arcDashCount < 1) return;

        float r = GetLocalSurfaceRadius() * arcDashRadialOffset;

        Vector3 a = fromLocalDir.normalized;
        Vector3 b = toLocalDir.normalized;

        // Total angular sweep along the great circle (radians).  If a == b
        // there's no arc to draw; bail out cleanly.
        float dot = Mathf.Clamp(Vector3.Dot(a, b), -1f, 1f);
        float totalAngle = Mathf.Acos(dot);
        if (totalAngle < 1e-4f) return;

        float fill = Mathf.Clamp(arcDashFill, 0.05f, 1f);

        for (int i = 0; i < arcDashCount; i++)
        {
            // Each segment occupies [t0, t1] in slerp parameter [0, 1].
            // The dash fills the FIRST `fill` fraction of that segment.
            float t0 = (float)i       / arcDashCount;
            float t1 = t0 + fill * (1f / arcDashCount);

            Vector3 d0 = Vector3.Slerp(a, b, t0).normalized;
            Vector3 d1 = Vector3.Slerp(a, b, t1).normalized;

            // Dash midpoint and tangent.  The chord between d0 and d1 (on the
            // unit sphere) gives both — its midpoint normalized is the on-arc
            // center, and its direction is the tangent.
            Vector3 midDir = ((d0 + d1) * 0.5f).normalized;
            Vector3 chord  = (d1 - d0);
            float   chordLen = chord.magnitude;
            if (chordLen < 1e-5f) continue;
            Vector3 tangent = chord / chordLen;

            // Dash length in world units = chord length scaled by the radius
            // we're floating at.  d0/d1 are unit vectors, so chordLen is in
            // unit-sphere units — multiply by r to land on the globe surface.
            float dashLength = chordLen * r;

            GameObject dash;
            if (arcDashPrefab != null)
            {
                dash = Instantiate(arcDashPrefab);
            }
            else
            {
                dash = GameObject.CreatePrimitive(PrimitiveType.Cube);
                // Strip the collider — pure visual, must not block raycasts.
                var dc = dash.GetComponent<Collider>();
                if (dc != null) Destroy(dc);
                var rend = dash.GetComponent<Renderer>();
                if (rend != null)
                {
                    Shader sh = Shader.Find("Unlit/Color");
                    if (sh == null) sh = Shader.Find("Standard");
                    rend.material = new Material(sh);
                    rend.material.color = arcDashColor;
                }
            }

            dash.transform.SetParent(transform, false);
            dash.transform.localPosition = midDir * r;
            // Orient so the dash's local +Z runs along the arc tangent and its
            // local +Y points outward (away from the globe centre).  LookRotation
            // takes (forward, up); midDir as up keeps the dash hugging the surface.
            dash.transform.localRotation = Quaternion.LookRotation(tangent, midDir);
            dash.transform.localScale    = new Vector3(arcDashThickness, arcDashThickness, dashLength);
            target.Add(dash);
        }
    }

    void DestroyArcDots()
    {
        for (int i = 0; i < arcDashes.Count; i++)
            if (arcDashes[i] != null) Destroy(arcDashes[i]);
        arcDashes.Clear();
    }

    // -----------------------------------------------------------------------
    // Historical Rounds (end-of-game review)
    //
    // GuessManager calls ShowHistoricalRounds once, after the FINAL round's
    // reveal animation completes, with every prior round's (guess, correct)
    // pair.  We populate the globe with a guess pin + correct pin + dashed
    // arc for each, parented to the globe so they rotate with it like the
    // active-round markers.
    //
    // The current round's pins+arc are NOT included — they're already on
    // display from the per-round animation, and re-instantiating them would
    // double them up.  ResetGlobe wipes the historical set when guess mode
    // re-opens for a new round (i.e. when StartNewGame loads round 1).
    // -----------------------------------------------------------------------
    public void ShowHistoricalRounds(IList<RoundResult> rounds)
    {
        ClearHistoricalRounds();

        if (rounds == null || rounds.Count == 0) return;
        if (markerPrefab == null || correctMarkerPrefab == null)
        {
            Debug.LogWarning("GlobeInteractable.ShowHistoricalRounds: marker prefab(s) not assigned — skipping historical pins.");
            return;
        }

        for (int i = 0; i < rounds.Count; i++)
        {
            RoundResult r = rounds[i];
            Vector3 guessDir   = LatLngToLocalDir(r.guessLat,   r.guessLng);
            Vector3 correctDir = LatLngToLocalDir(r.correctLat, r.correctLng);

            historicalRoundObjects.Add(InstantiatePinAtDir(markerPrefab,        guessDir));
            historicalRoundObjects.Add(InstantiatePinAtDir(correctMarkerPrefab, correctDir));

            // Append all dashes for this round's arc into the same historical
            // bucket so a single ClearHistoricalRounds() call can wipe everything.
            BuildArcDashesInto(guessDir, correctDir, historicalRoundObjects);
        }
    }

    // Instantiate a pin prefab at the given globe-local surface direction.
    // Mirrors PlaceCorrectMarker's setup (parenting, rotation, layer, seating)
    // but doesn't touch the activeMarker/correctMarker fields, so it can be
    // used to spawn arbitrary extra pins for the end-of-game review.
    GameObject InstantiatePinAtDir(GameObject prefab, Vector3 localDir)
    {
        float localRadius = GetLocalSurfaceRadius();
        GameObject pin = Instantiate(prefab);
        pin.transform.SetParent(transform, false);
        pin.transform.localPosition = localDir * localRadius;
        pin.transform.localRotation = Quaternion.FromToRotation(Vector3.up, localDir);
        SetLayerRecursively(pin, LayerMask.NameToLayer("Ignore Raycast"));

        Vector3 surfaceWorld = transform.TransformPoint(localDir * localRadius);
        SeatPinOnSurface(pin, surfaceWorld);
        return pin;
    }

    void ClearHistoricalRounds()
    {
        for (int i = 0; i < historicalRoundObjects.Count; i++)
            if (historicalRoundObjects[i] != null) Destroy(historicalRoundObjects[i]);
        historicalRoundObjects.Clear();
    }

    // -----------------------------------------------------------------------
    // Reset
    // -----------------------------------------------------------------------

    public void ResetGlobe()
    {
        // Stop any in-progress reveal animation so a new round starts cleanly.
        StopAllCoroutines();
        IsAnimatingResults      = false;
        DestroyArcDots();
        ClearHistoricalRounds();

        currentYaw              = 0f;
        currentPitch            = 0f;
        transform.localRotation = Quaternion.identity;
        // Clamp into the legal range so an inspector misconfig (e.g. initialScale
        // accidentally set above maxScale) doesn't ship a globe outside the
        // joystick zoom limits.
        ScaleTarget.localScale  = Vector3.one * Mathf.Clamp(initialScale, minScale, maxScale);
        triggerWasPressed       = false;
        // Clear grab baselines so any grip already held when guess mode opens
        // doesn't apply a stale delta on the first frame.  HandleGrab will
        // re-record the baseline on its next "just entered" branch.
        wasLeftGripping         = false;
        wasRightGripping        = false;
        // Clear any leftover momentum so the new round starts perfectly still.
        liveYawSpeed            = 0f;
        livePitchSpeed          = 0f;
        momentumYawSpeed        = 0f;
        momentumPitchSpeed      = 0f;
        if (activeMarker != null) Destroy(activeMarker);
        if (correctMarker != null) Destroy(correctMarker);
        HasMarker = false;
    }
}
