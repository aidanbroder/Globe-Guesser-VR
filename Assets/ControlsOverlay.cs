using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

// Displays a two-image controls reference panel in world space.
//
// Behaviour:
//   • Appears automatically when the game starts and after each Play Again.
//   • Fades out smoothly when the user looks away beyond `lookAwayAngleDeg`.
//   • Y button (left controller) toggles it: shows + repositions if hidden,
//     closes if currently visible.
//   • Does NOT reappear between rounds — only on game / Play Again start.
//
// Setup:
//   1. Add this component to any persistent GameObject (e.g. your Managers object).
//   2. Assign leftControllerImage and rightControllerImage in the Inspector.
//   3. Call ControlsOverlay.Instance.ShowOverlay() from GuessManager.StartNewGame().
public class ControlsOverlay : MonoBehaviour
{
    public static ControlsOverlay Instance;

    [Header("Control Images — Normal Mode")]
    [Tooltip("Texture for the left controller diagram in normal (street view) mode.")]
    public Texture2D leftControllerImage;
    [Tooltip("Texture for the right controller diagram in normal (street view) mode.")]
    public Texture2D rightControllerImage;

    [Header("Control Images — Guess Mode")]
    [Tooltip("Texture for the left controller diagram while the globe / guess panel is open.")]
    public Texture2D leftControllerGuessImage;
    [Tooltip("Texture for the right controller diagram while the globe / guess panel is open.")]
    public Texture2D rightControllerGuessImage;

    [Header("Display Settings")]
    [Tooltip("Distance in front of the camera (meters) where the panel spawns.")]
    public float panelDistance = 1.8f;

    [Tooltip("How far (degrees) the user can look away before the panel starts fading. " +
             "45° is roughly 'still roughly facing it'; 30° is tighter.")]
    public float lookAwayAngleDeg = 45f;

    [Tooltip("Alpha fade speed — higher is snappier.")]
    public float fadeSpeed = 2.5f;

    [Header("Panel Size (meters)")]
    [Tooltip("Width of each controller image in world space (meters).")]
    public float imageWidth  = 0.40f;
    [Tooltip("Height of each controller image in world space (meters).")]
    public float imageHeight = 0.50f;
    [Tooltip("Gap between the two images (meters).")]
    public float imageGap    = 0.06f;

    // -----------------------------------------------------------------------
    // Internal state
    // -----------------------------------------------------------------------

    private GameObject  overlayRoot;
    private CanvasGroup canvasGroup;
    private RawImage    leftImg;
    private RawImage    rightImg;

    // True when the overlay should be visible (fades in).  Set to false when
    // the user looks away or presses Y to close.
    private bool isShowing = false;

    // Set by RequestShowOnNextLoad(); consumed (and cleared) by NotifyLoadComplete()
    // so the overlay only appears after tiles finish downloading, not immediately.
    private bool pendingShow = false;

    // Flipped to true the first time the player opens guess mode so the
    // auto-show only fires once per session.
    private bool hasShownGuessTutorial = false;

    private InputAction yButtonAction;

    // -----------------------------------------------------------------------
    // Unity lifecycle
    // -----------------------------------------------------------------------

    void Awake()
    {
        if (Instance == null) Instance = this;
        else { Destroy(gameObject); return; }
    }

    void Start()
    {
        yButtonAction = new InputAction("YButton",
            binding: "<XRController>{LeftHand}/{SecondaryButton}");
        yButtonAction.Enable();

        BuildOverlayPanel();
        RequestShowOnNextLoad();    // will show once StreetViewSkybox signals tiles are ready
    }

    void OnDestroy()
    {
        yButtonAction?.Disable();
    }

    // -----------------------------------------------------------------------
    // Update — fade and gaze tracking
    // -----------------------------------------------------------------------

    void Update()
    {
        if (overlayRoot == null) return;

        // Y button: toggle — pick the image set matching the current mode
        if (yButtonAction != null && yButtonAction.WasPressedThisFrame())
        {
            if (isShowing)
                isShowing = false;          // close
            else
                ShowOverlay();              // reposition + open (textures set inside)
        }

        // Once the panel is fully invisible and inactive, nothing more to do.
        if (!overlayRoot.activeSelf) return;

        // Gaze check — if the user looks away, start fading out.
        if (isShowing && !IsLookingAtPanel())
            isShowing = false;

        // Drive alpha toward target.
        float targetAlpha = isShowing ? 1f : 0f;
        canvasGroup.alpha = Mathf.MoveTowards(
            canvasGroup.alpha, targetAlpha, fadeSpeed * Time.deltaTime);

        // Once fully faded out, deactivate so it doesn't render at all.
        if (canvasGroup.alpha <= 0f)
            overlayRoot.SetActive(false);
    }

    // -----------------------------------------------------------------------
    // Public API — called by GuessManager
    // -----------------------------------------------------------------------

    // Called by GuessManager.EnterGuessMode() — shows the guess-mode controls for
    // 5 seconds the very first time the player opens the globe, then fades out.
    // Does nothing on subsequent guess-mode opens so it isn't intrusive every round.
    public void OnFirstGuessMode()
    {
        if (hasShownGuessTutorial) return;
        hasShownGuessTutorial = true;
        ShowOverlay();                          // IsGuessing is true here → guess images
        StartCoroutine(AutoHideAfter(5f));
    }

    IEnumerator AutoHideAfter(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        isShowing = false;      // triggers the fade-out in Update
    }

    // Queue the overlay to appear the next time NotifyLoadComplete() is called.
    // Use this instead of ShowOverlay() when you want to wait for tiles to finish.
    public void RequestShowOnNextLoad()
    {
        pendingShow = true;
    }

    // Called by StreetViewSkybox once a panorama has successfully loaded.
    // Shows the overlay if RequestShowOnNextLoad() was queued, then clears the flag.
    public void NotifyLoadComplete()
    {
        if (!pendingShow) return;
        pendingShow = false;
        ShowOverlay();
    }

    // Show (or re-show) the overlay, repositioning it in front of the user's
    // current gaze.  Automatically picks the normal or guess-mode image set
    // based on whether GuessManager reports the player is currently guessing.
    // Safe to call even if the panel is already visible.
    public void ShowOverlay()
    {
        if (overlayRoot == null) return;

        bool guessing = GuessManager.Instance != null && GuessManager.Instance.IsGuessing;
        SetImagesForMode(guessing);

        overlayRoot.SetActive(true);
        PositionInFrontOfUser();
        isShowing = true;
    }

    // Swap the two RawImage textures to match the current game mode.
    void SetImagesForMode(bool guessMode)
    {
        if (leftImg  != null) leftImg.texture  = guessMode ? leftControllerGuessImage  : leftControllerImage;
        if (rightImg != null) rightImg.texture = guessMode ? rightControllerGuessImage : rightControllerImage;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    // Place the panel directly in front of the camera, yaw-only (no pitch/roll),
    // at the authored Y height of wherever Camera.main currently sits.
    void PositionInFrontOfUser()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 forward = cam.transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
        forward.Normalize();

        // Preserve the camera's Y so the panel sits at eye level.
        Vector3 pos   = cam.transform.position + forward * panelDistance;
        pos.y         = cam.transform.position.y;  // eye-level

        overlayRoot.transform.position = pos;
        // Face toward the user (panel's +Z points at camera).
        overlayRoot.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
    }

    // Returns true if Camera.main's forward is within `lookAwayAngleDeg` of the
    // direction from the camera to the panel centre.
    bool IsLookingAtPanel()
    {
        Camera cam = Camera.main;
        if (cam == null || overlayRoot == null) return false;

        Vector3 toPanel = (overlayRoot.transform.position - cam.transform.position).normalized;
        return Vector3.Angle(cam.transform.forward, toPanel) <= lookAwayAngleDeg;
    }

    // -----------------------------------------------------------------------
    // Panel construction — done once in Start so the Inspector only needs
    // Texture2D references; no prefab or scene hierarchy required.
    // -----------------------------------------------------------------------

    void BuildOverlayPanel()
    {
        // ── Root: world-space canvas ────────────────────────────────────────
        overlayRoot = new GameObject("ControlsOverlayPanel");

        Canvas canvas       = overlayRoot.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.WorldSpace;

        // CanvasGroup drives the alpha fade without touching individual elements.
        canvasGroup                 = overlayRoot.AddComponent<CanvasGroup>();
        canvasGroup.alpha           = 0f;
        canvasGroup.interactable    = false;
        canvasGroup.blocksRaycasts  = false;

        // With localScale = 1, RectTransform units == world-space metres.
        RectTransform rootRT = overlayRoot.GetComponent<RectTransform>();
        float totalWidth     = imageWidth * 2f + imageGap;
        rootRT.sizeDelta     = new Vector2(totalWidth, imageHeight);
        rootRT.localScale    = Vector3.one;

        // ── Two image children ──────────────────────────────────────────────
        // Left image sits left of centre; right image sits right of centre.
        float halfGap = imageGap  * 0.5f;
        float halfW   = imageWidth * 0.5f;

        leftImg  = CreateImageChild("LeftControllerImage",  leftControllerImage,
            new Vector2(-(halfW + halfGap), 0f));

        rightImg = CreateImageChild("RightControllerImage", rightControllerImage,
            new Vector2( (halfW + halfGap), 0f));

        overlayRoot.SetActive(false);   // hidden until ShowOverlay() is called
    }

    RawImage CreateImageChild(string goName, Texture2D tex, Vector2 centreOffset)
    {
        GameObject obj = new GameObject(goName);
        obj.transform.SetParent(overlayRoot.transform, false);

        RawImage img  = obj.AddComponent<RawImage>();
        img.texture   = tex;           // null is fine — shows white rect as placeholder

        RectTransform rt    = obj.GetComponent<RectTransform>();
        rt.sizeDelta        = new Vector2(imageWidth, imageHeight);
        rt.anchoredPosition = centreOffset;

        return img;
    }
}
