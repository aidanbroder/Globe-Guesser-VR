using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using TMPro;

// Per-round snapshot of the player's guess and the actual location.  Stored
// in GuessManager.roundResults across the game so the final-round reveal can
// show every prior round's pins + arc on the globe at once.
[System.Serializable]
public struct RoundResult
{
    public float guessLat;
    public float guessLng;
    public float correctLat;
    public float correctLng;
}

public class GuessManager : MonoBehaviour
{
    public static GuessManager Instance;

    [Header("References")]
    public GameObject guessModePanel;    // parent of globe + guess UI
    public GlobeInteractable globe;
    public StreetViewSkybox streetViewSkybox;

    [Header("Guess UI (child of GuessModePanel)")]
    public GameObject guessUI;           // the confirm/cancel buttons
    public Button confirmButton;
    public Button cancelButton;

    [Header("Results UI (child of GuessModePanel)")]
    public GameObject resultsUI;         // the results panel — starts hidden
    public TextMeshProUGUI distanceText;
    public TextMeshProUGUI scoreText;          // points scored this round
    public TextMeshProUGUI totalScoreText;     // running total across rounds
    public TextMeshProUGUI roundText;          // e.g. "Round 1 / 3" (optional)
    public Button nextRoundButton;

    [Tooltip("Globe scale snapped to when the results panel first appears, so the " +
             "globe sits cleanly inside the results rectangle.  Zoom is NOT locked " +
             "afterwards — the user can freely zoom in/out from this starting size " +
             "to inspect the red/green pins more closely.")]
    public float resultsGlobeScale = 0.6f;

    [Header("Final Round")]
    [Tooltip("Shown in place of the Next Round button on the last round. " +
             "Wire its OnClick to GuessManager.StartNewGame in the inspector.")]
    public Button playAgainButton;

    [Header("Scoring")]
    [Tooltip("Score formula: 1000 * exp(-(distanceKm / decayFactor)^decayPower).\n\n" +
             "decayFactor (km): characteristic distance at which the inner term hits 1. " +
             "Larger = more forgiving overall.  Default 7700 ≈ a 580 km miss (LA→Sacramento) " +
             "still scores ~950.")]
    public float decayFactor = 7700f;
    [Tooltip("Power applied inside the exponent.  >1 flattens the curve near zero (small " +
             "misses barely cost anything) AND steepens the drop at large distances (so a " +
             "wrong-continent guess still scores near zero).  =1 reproduces the old pure " +
             "exponential.  Default 1.15 paired with decayFactor=7700 gives a GeoGuessr-ish feel.")]
    public float decayPower  = 1.15f;
    public int   totalRounds = 3;        // how many rounds make up one game

    // Display-only conversion.  Scoring stays in km (decayFactor unchanged) so
    // the existing point-decay tuning isn't disturbed.
    private const float KM_TO_MILES = 0.621371f;

    [Header("Audio")]
    [Tooltip("Played once whenever the player clicks Confirm, Cancel, Next Round, or Play Again.")]
    public AudioClip buttonClickClip;

    [Header("Panel Placement")]
    [Tooltip("Meters in front of the camera where the guess panel spawns. " +
             "Only X/Z are changed — the panel's authored Y height is preserved.")]
    public float panelDistance = 1.5f;

    public bool IsGuessing { get; private set; }

    private AudioSource audioSource;
    private GameObject locomotionObject;
    private int totalScore;
    private int roundNumber;
    private InputAction xButtonAction;

    // Every confirmed round in the current game, in order.  Cleared by
    // StartNewGame.  After the final round's reveal animation finishes we
    // hand every prior round to the globe so the player can review their
    // full game.
    private List<RoundResult> roundResults = new List<RoundResult>();

    void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    void Start()
    {
        audioSource = GetComponent<AudioSource>();
        if (audioSource == null) audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;

        locomotionObject = GameObject.Find("Locomotion");
        if (guessModePanel != null) guessModePanel.SetActive(false);
        if (resultsUI != null) resultsUI.SetActive(false);
        if (playAgainButton != null) playAgainButton.gameObject.SetActive(false);

        // X button (left controller) toggles guess mode on/off
        xButtonAction = new InputAction("XButton", binding: "<XRController>{LeftHand}/{PrimaryButton}");
        xButtonAction.Enable();
    }

    void OnDestroy()
    {
        xButtonAction?.Disable();
    }

    void Update()
    {
        if (xButtonAction != null && xButtonAction.WasPressedThisFrame())
        {
            if (IsGuessing)
            {
                // Only allow X to bail out during the placement phase.  Once the
                // user has confirmed a guess (results UI showing) or the game is
                // over (final-score UI showing), the X button is ignored — they
                // must use Next Round / Play Again to advance.  Letting them exit
                // mid-results desyncs roundNumber/totalScore from the active pano
                // and creates a class of bugs (stale totals, double-counted rounds,
                // pin still parented to a hidden globe, etc.).
                bool inResultsPhase = (resultsUI != null && resultsUI.activeSelf);
                if (!inResultsPhase) ExitGuessMode();
            }
            else
            {
                EnterGuessMode();
            }
        }

        // Gate the confirm/guess button on whether the user has actually placed
        // a marker.  Setting interactable=false makes Unity tint the button with
        // its "Disabled Color" (set this to grey in the Button's Colors block in
        // the inspector — the green Normal Color you authored stays for the
        // enabled state).  guessUI.activeSelf check avoids flickering interactable
        // on the (hidden) button while the results UI is up.
        if (confirmButton != null && guessUI != null && guessUI.activeSelf)
            confirmButton.interactable = globe != null && globe.HasMarker;
    }

    public void EnterGuessMode()
    {
        if (locomotionObject != null) locomotionObject.SetActive(false);

        IsGuessing = true;

        // First time only: auto-show the guess controls for 5 seconds.
        if (ControlsOverlay.Instance != null) ControlsOverlay.Instance.OnFirstGuessMode();
        guessModePanel.SetActive(true);
        PositionPanelInFrontOfUser();   // one-shot — panel does NOT follow the head after this
        guessUI.SetActive(true);
        resultsUI.SetActive(false);
        globe.ResetGlobe();

        // Confirm button starts disabled — flips on once HasMarker == true (see Update).
        if (confirmButton != null) confirmButton.interactable = false;
    }

    // -----------------------------------------------------------------------
    // Place the guess-mode panel at the user's current gaze direction.
    // Yaw-only: pitch/roll are stripped so the panel stays upright and at its
    // authored Y height regardless of whether the user is looking up/down.
    // -----------------------------------------------------------------------
    void PositionPanelInFrontOfUser()
    {
        Camera headCam = Camera.main;
        if (headCam == null)
        {
            Debug.LogWarning("GuessManager: Camera.main is null — can't reposition guess panel. " +
                             "Tag the XR head camera as MainCamera.");
            return;
        }

        // Flatten the head's forward vector onto the horizontal plane so we
        // extract only the yaw.  If the user happens to be staring straight
        // up or down, bail out and leave the panel wherever it was — a zero
        // forward would produce garbage.
        Vector3 forward = headCam.transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.0001f) return;
        forward.Normalize();

        Vector3 headPos     = headCam.transform.position;
        Vector3 currentPos  = guessModePanel.transform.position;
        Vector3 newPos      = headPos + forward * panelDistance;
        newPos.y = currentPos.y;        // preserve the authored height exactly

        guessModePanel.transform.position = newPos;
        // Face the user: panel's +Z points away from the camera so its front
        // side (the one with the globe / buttons visible) is what the user sees.
        guessModePanel.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
    }

    void PlayButtonSound()
    {
        if (audioSource != null && buttonClickClip != null)
            audioSource.PlayOneShot(buttonClickClip);
    }

    // Called by the Cancel button's OnClick (instead of ExitGuessMode directly)
    // so the click sound plays on button press but NOT when the X button bails out.
    public void CancelGuess()
    {
        PlayButtonSound();
        ExitGuessMode();
    }

    public void ConfirmGuess()
    {
        PlayButtonSound();

        if (!globe.HasMarker)
        {
            Debug.Log("Place a marker on the globe first!");
            return;
        }

        Vector2 guess = globe.GetGuessLatLng();
        StreetViewLocation current = LocationManager.Instance.CurrentLocation;

        float distance      = HaversineDistance(guess.x, guess.y, current.latitude, current.longitude);
        float distanceMiles = distance * KM_TO_MILES;
        int   score         = CalculateScore(distance);
        totalScore += score;
        roundNumber++;

        // Record this round so the final-round reveal can show all prior pins.
        roundResults.Add(new RoundResult
        {
            guessLat   = guess.x,
            guessLng   = guess.y,
            correctLat = current.latitude,
            correctLng = current.longitude,
        });

        Debug.Log($"Guessed: ({guess.x:F2}, {guess.y:F2}) — Actual: ({current.latitude:F2}, {current.longitude:F2}) — Distance: {distance:F0} km ({distanceMiles:F0} mi) — Score: {score}");

        // Switch from guess UI to results UI.  Hiding guessUI takes the
        // confirm + cancel buttons offscreen automatically.  The reveal
        // animation owns the green marker placement and final globe scale —
        // GuessManager just kicks it off and locks the Next Round button
        // until it finishes so the player can't skip past the reveal.
        guessUI.SetActive(false);
        resultsUI.SetActive(true);

        if (globe != null)
            StartCoroutine(RunResultsRevealAnimation(current.latitude, current.longitude));

        distanceText.text = $"{distanceMiles:F0} mi away";
        scoreText.text    = $"{score} points";
        if (totalScoreText != null)
            totalScoreText.text = $"Total: {totalScore} / {roundNumber * 1000}";
        if (roundText != null)
            roundText.text = $"Round {roundNumber} / {totalRounds}";

        // If this was the final round, swap the Next Round button out for the
        // Play Again button.  The player still sees the round's results panel
        // (distance, score, correct-marker placement) alongside the restart CTA.
        // playAgainButton is wired to StartNewGame via its OnClick in the inspector.
        if (roundNumber >= totalRounds)
        {
            if (nextRoundButton  != null) nextRoundButton.gameObject.SetActive(false);
            if (playAgainButton  != null) playAgainButton.gameObject.SetActive(true);
        }
    }

    // Drive the globe's reveal animation and lock Next Round / Play Again until
    // it finishes.  Without the lock the player could click straight through the
    // pause-on-correct pause and skip the punchline of the round.
    IEnumerator RunResultsRevealAnimation(float correctLat, float correctLng)
    {
        if (nextRoundButton != null) nextRoundButton.interactable = false;
        if (playAgainButton != null) playAgainButton.interactable = false;

        yield return globe.AnimateResultsReveal(correctLat, correctLng, resultsGlobeScale);

        // After the FINAL round's animation completes, populate the globe
        // with every PRIOR round's guess+correct pins and arc.  The current
        // round is already on display from the animation, so we exclude it.
        // Done here (not before yield) so the player gets the dramatic
        // single-round reveal first, and only then sees the full picture.
        if (roundNumber >= totalRounds && globe != null && roundResults.Count > 1)
        {
            var prior = new List<RoundResult>(roundResults.Count - 1);
            for (int i = 0; i < roundResults.Count - 1; i++) prior.Add(roundResults[i]);
            globe.ShowHistoricalRounds(prior);
        }

        if (nextRoundButton != null) nextRoundButton.interactable = true;
        if (playAgainButton != null) playAgainButton.interactable = true;
    }

    public void NextRound()
    {
        PlayButtonSound();
        // Final-round case is handled by playAgainButton (wired to StartNewGame
        // directly), so by the time NextRound fires we know there's another
        // round to load.  Guard anyway in case OnClick is ever wired wrong.
        if (roundNumber >= totalRounds)
        {
            Debug.LogWarning("GuessManager.NextRound called on final round — should be Play Again.");
            StartNewGame();
            return;
        }

        ExitGuessMode();
        if (streetViewSkybox != null) streetViewSkybox.LoadNewLocation();
        else Debug.LogWarning("GuessManager: StreetViewSkybox reference not set — cannot load next location.");
    }

    // Hook this up to the Play Again button's OnClick in the Inspector.
    public void StartNewGame()
    {
        PlayButtonSound();
        // Queue the controls overlay to appear once the new panorama finishes loading.
        if (ControlsOverlay.Instance != null) ControlsOverlay.Instance.RequestShowOnNextLoad();
        totalScore  = 0;
        roundNumber = 0;
        roundResults.Clear();

        // Restore the Next Round / Play Again button visibility for the new game.
        if (nextRoundButton != null) nextRoundButton.gameObject.SetActive(true);
        if (playAgainButton != null) playAgainButton.gameObject.SetActive(false);

        if (LocationManager.Instance != null) LocationManager.Instance.ResetGame();

        // Close the guess panel and load a fresh Street View location.
        ExitGuessMode();
        if (streetViewSkybox != null) streetViewSkybox.LoadNewLocation();
        else Debug.LogWarning("GuessManager: StreetViewSkybox reference not set — cannot load next location.");
    }

    public void ExitGuessMode()
    {
        if (locomotionObject != null) locomotionObject.SetActive(true);

        IsGuessing = false;
        guessModePanel.SetActive(false);
    }

    // Score: 1000 * exp(-(distance / decay)^power)
    // With defaults (decay=7700, power=1.15):
    //   0 km = 1000, 580 km ≈ 950, 1000 km ≈ 908, 5000 km ≈ 546, 20000 km ≈ 51
    // The power flattens the curve near zero (small misses don't get punished
    // hard) and steepens the tail (antipodal guesses still score near zero).
    int CalculateScore(float distanceKm)
    {
        float decay = Mathf.Max(1f, decayFactor);            // guard div-by-zero
        float power = Mathf.Max(0.1f, decayPower);           // sane floor
        float ratio = distanceKm / decay;
        float raw   = 1000f * Mathf.Exp(-Mathf.Pow(ratio, power));
        return Mathf.RoundToInt(raw);
    }

    // Haversine formula — returns distance in km
    float HaversineDistance(float lat1, float lon1, float lat2, float lon2)
    {
        const float R = 6371f;
        float dLat = Mathf.Deg2Rad * (lat2 - lat1);
        float dLon = Mathf.Deg2Rad * (lon2 - lon1);
        float a = Mathf.Sin(dLat / 2) * Mathf.Sin(dLat / 2)
                + Mathf.Cos(Mathf.Deg2Rad * lat1) * Mathf.Cos(Mathf.Deg2Rad * lat2)
                * Mathf.Sin(dLon / 2) * Mathf.Sin(dLon / 2);
        return R * 2f * Mathf.Atan2(Mathf.Sqrt(a), Mathf.Sqrt(1 - a));
    }
}
