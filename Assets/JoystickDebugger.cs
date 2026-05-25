using UnityEngine;
using UnityEngine.InputSystem;

public class JoystickDebugger : MonoBehaviour
{
    private InputAction rightStick;
    private InputAction leftStick;

    void OnEnable()
    {
        rightStick = new InputAction("RightStick", binding: "<XRController>{RightHand}/{Primary2DAxis}");
        leftStick  = new InputAction("LeftStick",  binding: "<XRController>{LeftHand}/{Primary2DAxis}");
        rightStick.Enable();
        leftStick.Enable();
    }

    void OnDisable()
    {
        rightStick?.Disable();
        leftStick?.Disable();
    }

    void Update()
    {
        Vector2 right = rightStick.ReadValue<Vector2>();
        Vector2 left  = leftStick.ReadValue<Vector2>();

        if (right.magnitude > 0.1f)
            Debug.Log($"RIGHT stick: {right}");

        if (left.magnitude > 0.1f)
            Debug.Log($"LEFT stick: {left}");
    }
}
