using UnityEngine;
using UnityEngine.XR;
using System.Collections;
using System.Collections.Generic;

[RequireComponent(typeof(CanvasGroup))]
public class FadeOnAnyAction : MonoBehaviour
{
    [Header("References")]
    public Transform headTransform;

    [Header("Fade Settings")]
    public float fadeDuration = 1f;
    public float startDelay = 0.5f;

    [Header("Sensitivity")]
    public float headRotationThreshold = 10f;
    public float headPositionThreshold = 0.1f;
    public float controllerInputThreshold = 0.2f;

    private CanvasGroup canvasGroup;
    private Quaternion startRotation;
    private Vector3 startPosition;
    private bool fading = false;
    private bool listening = false;
    private bool isVisible = true;
    private bool yButtonWasPressed = false; // for edge detection

    void Start()
    {
        canvasGroup = GetComponent<CanvasGroup>();

        if (headTransform == null && Camera.main != null)
            headTransform = Camera.main.transform;

        StartCoroutine(BeginListening());
    }

    IEnumerator BeginListening()
    {
        yield return new WaitForSeconds(startDelay);
        CapturePose();
        listening = true;
    }

    void CapturePose()
    {
        startRotation = headTransform.rotation;
        startPosition = headTransform.position;
    }

    void Update()
    {
        // Always check the Y button so the user can summon it back anytime
        bool yPressed = IsYButtonPressed();

        // Edge detection — only trigger on the press, not while held
        if (yPressed && !yButtonWasPressed && !isVisible && !fading)
        {
            StartCoroutine(FadeIn());
        }
        yButtonWasPressed = yPressed;

        if (!listening || fading || !isVisible) return;

        if (HeadMoved() || ControllerInput())
        {
            StartCoroutine(FadeOut());
        }
    }

    bool IsYButtonPressed()
    {
        List<InputDevice> leftHand = new List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(
            InputDeviceCharacteristics.Controller | InputDeviceCharacteristics.Left,
            leftHand);

        foreach (var device in leftHand)
        {
            if (device.TryGetFeatureValue(CommonUsages.secondaryButton, out bool y) && y)
                return true;
        }
        return false;
    }

    bool HeadMoved()
    {
        float angle = Quaternion.Angle(startRotation, headTransform.rotation);
        float distance = Vector3.Distance(startPosition, headTransform.position);
        return angle > headRotationThreshold || distance > headPositionThreshold;
    }

    bool ControllerInput()
    {
        List<InputDevice> devices = new List<InputDevice>();
        InputDevices.GetDevicesWithCharacteristics(
            InputDeviceCharacteristics.Controller, devices);

        foreach (var device in devices)
        {
            // Skip the Y button on the left hand — that's our summon button, not a "fade" trigger
            bool isLeft = (device.characteristics & InputDeviceCharacteristics.Left) != 0;

            if (device.TryGetFeatureValue(CommonUsages.triggerButton, out bool trigger) && trigger) return true;
            if (device.TryGetFeatureValue(CommonUsages.gripButton, out bool grip) && grip) return true;
            if (device.TryGetFeatureValue(CommonUsages.primaryButton, out bool primary) && primary) return true;

            // Only count secondary button as "action" on the RIGHT hand (B button), not left (Y)
            if (!isLeft && device.TryGetFeatureValue(CommonUsages.secondaryButton, out bool secondary) && secondary) return true;

            if (device.TryGetFeatureValue(CommonUsages.menuButton, out bool menu) && menu) return true;
            if (device.TryGetFeatureValue(CommonUsages.trigger, out float triggerVal) && triggerVal > controllerInputThreshold) return true;
            if (device.TryGetFeatureValue(CommonUsages.grip, out float gripVal) && gripVal > controllerInputThreshold) return true;
            if (device.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 stick) && stick.magnitude > controllerInputThreshold) return true;
            if (device.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool stickClick) && stickClick) return true;
        }

        return false;
    }

    IEnumerator FadeOut()
    {
        fading = true;
        float elapsed = 0f;
        float startAlpha = canvasGroup.alpha;

        while (elapsed < fadeDuration)
        {
            elapsed += Time.deltaTime;
            canvasGroup.alpha = Mathf.Lerp(startAlpha, 0f, elapsed / fadeDuration);
            yield return null;
        }

        canvasGroup.alpha = 0f;
        canvasGroup.interactable = false;
        canvasGroup.blocksRaycasts = false;
        isVisible = false;
        fading = false;
    }

    IEnumerator FadeIn()
    {
        fading = true;
        listening = false; // pause motion checks while fading in
        float elapsed = 0f;
        float startAlpha = canvasGroup.alpha;

        canvasGroup.interactable = true;
        canvasGroup.blocksRaycasts = true;

        while (elapsed < fadeDuration)
        {
            elapsed += Time.deltaTime;
            canvasGroup.alpha = Mathf.Lerp(startAlpha, 1f, elapsed / fadeDuration);
            yield return null;
        }

        canvasGroup.alpha = 1f;
        isVisible = true;
        fading = false;

        // Reset baseline pose and re-arm motion detection after a short grace period
        yield return new WaitForSeconds(startDelay);
        CapturePose();
        listening = true;
    }
}