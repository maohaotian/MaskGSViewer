// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Smooth locomotion and snap-turn for the VR Gaussian Splatting viewer.
///
/// Controls (VR):
///   Left stick         → smooth move (relative to HMD facing direction, XZ plane)
///   Right stick X      → continuous turn
///   Right stick Y      → fly up / down (useful for inspecting splats from above)
///
/// Keyboard fallback (editor / desktop, no HMD):
///   W / A / S / D      → move forward / left / back / right
///   Space / C          → move up / down
///   Shift              → full-speed movement
///   Also works while XR is active, useful when controllers are unavailable.
///   Left / right click → capture mouse
///   Middle mouse drag  → drag the camera
/// </summary>
[RequireComponent(typeof(VRRig))]
public class VRLocomotion : MonoBehaviour
{
    [Header("Smooth Movement")]
    [Tooltip("Horizontal move speed in metres per second.")]
    public float moveSpeed = 2.5f;

    [Tooltip("Vertical fly speed in metres per second (right stick Y).")]
    public float flySpeed = 1.5f;

    [Tooltip("Analogue stick dead-zone radius (0–1).")]
    [Range(0f, 0.5f)]
    public float stickDeadzone = 0.2f;

    [Header("Turning")]
    [Tooltip("Continuous yaw rotation speed in degrees per second from the right stick X axis.")]
    public float turnSpeed = 90f;

    [Header("Mouse Look (desktop fallback)")]
    [Tooltip("Mouse look sensitivity when using the keyboard fallback.")]
    public float mouseSensitivity = 2f;

    [Tooltip("Maximum vertical look angle in desktop mode. Keeps FPS-style mouse look from flipping at the poles.")]
    [Range(1f, 89.9f)]
    public float maxMousePitch = 89f;

    [Tooltip("Camera translation speed while dragging with the mouse.")]
    public float dragSpeed = 0.01f;

    [Tooltip("Hide and lock the cursor while using desktop mode.")]
    public bool lockCursorInDesktopMode = true;

    // ── Private state ─────────────────────────────────────────────────────────

    const float MousePositionDeltaScale = 0.1f;

    VRRig  _rig;
    float  _mouseYaw;     // accumulated horizontal mouse look (desktop only)
    float  _mousePitch;   // accumulated vertical mouse look (desktop only)
    bool   _mouseLookCaptured;
    bool   _skipMouseLookFrame;
    VRFileBrowser _browser;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Awake()
    {
        _rig = GetComponent<VRRig>();
        _browser = FindAnyObjectByType<VRFileBrowser>();

        if (XRSettings.isDeviceActive)
            SyncDesktopPitchFromCamera();
        else
            ResetDesktopLook();
    }

    void SyncDesktopPitchFromCamera()
    {
        Camera cam = _rig != null ? _rig.xrCamera : Camera.main;
        if (cam != null)
        {
            float pitch = cam.transform.localEulerAngles.x;
            if (pitch > 180f)
                pitch -= 360f;
            _mousePitch = pitch;
        }

        _mouseYaw = NormalizeAngle(transform.eulerAngles.y);
    }

    public void ResetDesktopLook()
    {
        if (XRSettings.isDeviceActive)
            return;

        Camera cam = _rig != null ? _rig.xrCamera : Camera.main;
        if (cam != null)
        {
            cam.transform.localRotation = Quaternion.identity;
        }

        _mousePitch = 0f;
        _mouseYaw = NormalizeAngle(transform.eulerAngles.y);
    }

    public void SyncDesktopLookFromCurrentPose()
    {
        if (XRSettings.isDeviceActive)
            return;

        SyncDesktopPitchFromCamera();
    }

    void Update()
    {
        if (XRSettings.isDeviceActive)
        {
            VRMove();
            VRTurn();
            KeyboardMouseFallback(false);
        }
        else
        {
            KeyboardMouseFallback(true);
        }
    }

    // ── VR movement ───────────────────────────────────────────────────────────

    void VRMove()
    {
        if (_browser != null && _browser.IsOpen) return;

        Vector2 leftStick = ReadStick(XRNode.LeftHand);
        if (leftStick.magnitude <= stickDeadzone)
            return;

        // Use the camera's world-space axes so direction is always correct regardless
        // of XROrigin rotation (snap turns, spawn orientation, etc.)
        Camera cam = _rig != null ? _rig.xrCamera : Camera.main;
        if (cam == null) return;

        Vector3 headForward = cam.transform.forward;
        headForward.y = 0f;
        if (headForward.sqrMagnitude < 0.001f) return;
        headForward.Normalize();

        Vector3 headRight = cam.transform.right;
        headRight.y = 0f;
        headRight.Normalize();

        Vector3 move = (headForward * leftStick.y + headRight * leftStick.x)
                       * moveSpeed * Time.deltaTime;
        transform.position += move;
    }

    void VRTurn()
    {
        // Right stick is used by file browser when open
        if (_browser != null && _browser.IsOpen) return;

        Vector2 rightStick = ReadStick(XRNode.RightHand);

        // Horizontal axis → continuous rotate the XR Origin
        if (Mathf.Abs(rightStick.x) > stickDeadzone)
            transform.Rotate(0f, rightStick.x * turnSpeed * Time.deltaTime, 0f, Space.World);

        // Vertical axis → fly up / down
        if (Mathf.Abs(rightStick.y) > stickDeadzone)
            transform.position += Vector3.up * (rightStick.y * flySpeed * Time.deltaTime);
    }

    // ── Keyboard / mouse fallback ─────────────────────────────────────────────

    void KeyboardMouseFallback(bool allowMouseLook)
    {
        if (_browser != null && (_browser.IsOpen || _browser.WasOpenThisFrame))
            return;

        if (allowMouseLook)
            UpdateDesktopCursorState();

        // Translation
        float h = (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f);
        float v = (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f);
        float y = (Input.GetKey(KeyCode.Space) ? 1f : 0f) - (Input.GetKey(KeyCode.C) ? 1f : 0f);

        Camera cam = _rig != null ? _rig.xrCamera : Camera.main;

        Vector3 forward = cam != null ? cam.transform.forward : transform.forward;
        if (!allowMouseLook)
            forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f)
            forward = Vector3.forward;
        forward.Normalize();

        Vector3 right = cam != null ? cam.transform.right : transform.right;
        right.y = 0f;
        if (right.sqrMagnitude < 0.001f)
            right = Vector3.right;
        right.Normalize();

        Vector3 move = (right * h + forward * v + Vector3.up * y);
        if (move.sqrMagnitude > 0.01f)
        {
            bool sprint = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            float speedFactor = sprint ? 2f : (1f / 3f);
            transform.position += move.normalized * moveSpeed * speedFactor * Time.deltaTime;
        }

        bool canApplyMouseLook = allowMouseLook && (!lockCursorInDesktopMode || Cursor.lockState == CursorLockMode.Locked);
        if (canApplyMouseLook)
        {
            Vector2 mouseDelta = ReadDesktopMouseDelta();
            float mouseX = mouseDelta.x * mouseSensitivity;
            float mouseY = mouseDelta.y * mouseSensitivity;
            bool dragHeld = Input.GetMouseButton(2);

            if (dragHeld)
            {
                Vector3 dragRight = cam != null ? cam.transform.right : transform.right;
                Vector3 dragUp = cam != null ? cam.transform.up : Vector3.up;
                Vector3 dragMove = (-dragRight * mouseX - dragUp * mouseY) * dragSpeed;
                transform.position += dragMove;
                return;
            }

            _mouseYaw = NormalizeAngle(_mouseYaw + mouseX);
            transform.rotation = Quaternion.Euler(0f, _mouseYaw, 0f);

            _mousePitch -= mouseY;
            _mousePitch = Mathf.Clamp(_mousePitch, -maxMousePitch, maxMousePitch);

            if (cam != null)
                cam.transform.localRotation = Quaternion.Euler(_mousePitch, 0f, 0f);
        }
    }

    Vector2 ReadDesktopMouseDelta()
    {
        if (_skipMouseLookFrame)
        {
            _skipMouseLookFrame = false;
            return Vector2.zero;
        }

#if ENABLE_INPUT_SYSTEM
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse != null)
            return mouse.delta.ReadValue() * MousePositionDeltaScale;
#endif

#if UNITY_2023_2_OR_NEWER
        Vector3 delta = Input.mousePositionDelta;
        return new Vector2(delta.x, delta.y) * MousePositionDeltaScale;
#else
        return new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y"));
#endif
    }

    static float NormalizeAngle(float angle)
    {
        return Mathf.Repeat(angle + 180f, 360f) - 180f;
    }

    void UpdateDesktopCursorState()
    {
        if (!lockCursorInDesktopMode)
            return;

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            _mouseLookCaptured = false;
            _skipMouseLookFrame = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            return;
        }

        if (Cursor.lockState != CursorLockMode.Locked &&
            (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)))
        {
            _mouseLookCaptured = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            _skipMouseLookFrame = true;
        }
        else if (_mouseLookCaptured)
        {
            if (Cursor.lockState != CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.Locked;
                _skipMouseLookFrame = true;
            }
            Cursor.visible = false;
        }
    }

    void OnApplicationFocus(bool hasFocus)
    {
        if (!hasFocus || XRSettings.isDeviceActive || !lockCursorInDesktopMode || !_mouseLookCaptured)
            return;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        _skipMouseLookFrame = true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

static readonly List<InputDevice> s_devices = new(2);

/// <summary>Reads the primary 2D axis (thumbstick) from an XR controller node.</summary>
static Vector2 ReadStick(XRNode node)
{
    s_devices.Clear();
    InputDevices.GetDevicesAtXRNode(node, s_devices);
    if (s_devices.Count > 0 &&
        s_devices[0].TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 v))
        return v;
    return Vector2.zero;
}

    /// <summary>Returns the HMD's look direction projected flat onto the XZ plane.</summary>
    static Vector3 GetHMDForwardFlat()
    {
        s_devices.Clear();
        InputDevices.GetDevicesAtXRNode(XRNode.Head, s_devices);
        if (s_devices.Count > 0 &&
        s_devices[0].TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rot))
        {
            Vector3 fwd = rot * Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 0.001f)
                return fwd.normalized;
        }
        return Vector3.forward;
    }
}
