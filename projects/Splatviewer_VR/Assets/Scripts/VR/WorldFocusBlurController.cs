using UnityEngine;
using UnityEngine.Rendering;

[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
public sealed class WorldFocusBlurController : MonoBehaviour
{
    static readonly string[] s_SkyboxTexturePropertyNames = { "_Tex", "_MainTex", "_Cubemap" };

    public struct FocusEvaluation
    {
        public float FocusT;
        public float HorizontalAngle;
        public float VerticalAngle;
        public float AngularDistance;
        public float SignedHorizontalSlope;
        public float SignedVerticalSlope;
        public bool WorldAnchored;
        public bool UsesCapturedFrame;
    }

    [Header("Enable")]
    [Tooltip("Disabling this component restores the original image immediately.")]
    public bool effectEnabled = true;

    [Header("Focus Window")]
    [Tooltip("Half vertical FOV in degrees that stays fully sharp. Horizontal FOV is derived from the focus aspect.")]
    [Range(1f, 89f)]
    public float innerAngle = 20f;

    [Tooltip("Half vertical FOV in degrees where the outside reaches full peripheral strength.")]
    [Range(1f, 89f)]
    public float outerAngle = 42f;

    [Tooltip("Use the camera aspect at capture time for the rectangular FOV window.")]
    public bool useCameraAspectForFocus = true;

    [Tooltip("Used only when Use Camera Aspect For Focus is disabled.")]
    [Min(0.1f)]
    public float customFocusAspect = 1.7777778f;

    [Tooltip("Project the focus window onto a fixed world-space plane so translation in VR changes the mask spatially.")]
    public bool worldAnchoredFocus = true;

    [Tooltip("Treat the entire front hemisphere as clear. The rear hemisphere remains peripheral.")]
    public bool frontHemisphereOnly = false;

    [Tooltip("Distance from the captured camera position to the fixed focus plane.")]
    [Min(0.1f)]
    public float focusPlaneDistance = 5f;

    [Header("Blur")]
    [Tooltip("Maximum blur radius in pixels at the outside of the focus window.")]
    [Range(0f, 32f)]
    public float maxBlurPixels = 10f;

    [Tooltip("Extra darkening in the peripheral area.")]
    [Range(0f, 1f)]
    public float peripheralDim = 0.15f;

    [Header("Peripheral Skybox")]
    [Tooltip("When enabled, the peripheral area fades to skybox instead of a blurred copy of the scene.")]
    public bool replacePeripheralWithSkybox = true;

    [Tooltip("Use a cubemap from RenderSettings.skybox when that skybox exposes one.")]
    public bool useRenderSettingsSkybox = true;

    [Tooltip("Optional cubemap used for the peripheral skybox. If empty, a simple procedural sky gradient is used.")]
    public Cubemap skyboxCubemap;

    public Color skyboxZenithColor = new Color(0.36f, 0.55f, 0.85f, 1f);
    public Color skyboxHorizonColor = new Color(0.78f, 0.85f, 0.92f, 1f);
    public Color skyboxGroundColor = new Color(0.55f, 0.55f, 0.55f, 1f);

    [Min(0f)]
    public float skyboxExposure = 1f;

    [Header("Capture")]
    [Tooltip("Capture the current camera pose as the fixed world focus frame on enable.")]
    public bool captureOnEnable = true;

    [Tooltip("If enabled, this locks the focus window to the first camera position and rotation in world space.")]
    public bool lockToCapturedDirection = true;

    static readonly int s_EffectEnabledId = Shader.PropertyToID("_WorldFocusEffectEnabled");
    static readonly int s_FocusForwardId = Shader.PropertyToID("_WorldFocusForwardWS");
    static readonly int s_FocusRightId = Shader.PropertyToID("_WorldFocusRightWS");
    static readonly int s_FocusUpId = Shader.PropertyToID("_WorldFocusUpWS");
    static readonly int s_FocusOriginId = Shader.PropertyToID("_WorldFocusOriginWS");
    static readonly int s_CameraPositionId = Shader.PropertyToID("_WorldFocusCameraPositionWS");
    static readonly int s_CameraForwardId = Shader.PropertyToID("_WorldFocusCameraForwardWS");
    static readonly int s_CameraRightId = Shader.PropertyToID("_WorldFocusCameraRightWS");
    static readonly int s_CameraUpId = Shader.PropertyToID("_WorldFocusCameraUpWS");
    static readonly int s_FocusParamsId = Shader.PropertyToID("_WorldFocusParams");
    static readonly int s_CameraParamsId = Shader.PropertyToID("_WorldFocusCameraParams");
    static readonly int s_RectParamsId = Shader.PropertyToID("_WorldFocusRectParams");
    static readonly int s_DisplayParamsId = Shader.PropertyToID("_WorldFocusDisplayParams");
    static readonly int s_SkyboxCubemapId = Shader.PropertyToID("_WorldFocusSkyboxCubemap");
    static readonly int s_SkyboxZenithId = Shader.PropertyToID("_WorldFocusSkyboxZenithColor");
    static readonly int s_SkyboxHorizonId = Shader.PropertyToID("_WorldFocusSkyboxHorizonColor");
    static readonly int s_SkyboxGroundId = Shader.PropertyToID("_WorldFocusSkyboxGroundColor");

    Camera _camera;
    Vector3 _capturedOrigin = Vector3.zero;
    Vector3 _capturedForward = Vector3.forward;
    Vector3 _capturedRight = Vector3.right;
    Vector3 _capturedUp = Vector3.up;
    float _capturedAspect = 1.7777778f;
    bool _hasCapturedForward;
    bool _capturePending;

    void Awake()
    {
        _camera = GetComponent<Camera>();
    }

    void OnEnable()
    {
        _capturePending = captureOnEnable;
        _hasCapturedForward = false;
        Shader.SetGlobalFloat(s_EffectEnabledId, 0f);
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
    }

    void LateUpdate()
    {
        PushGlobals();
    }

    void OnDisable()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        Shader.SetGlobalFloat(s_EffectEnabledId, 0f);
    }

    void OnBeginCameraRendering(ScriptableRenderContext context, Camera renderingCamera)
    {
        if (_camera == null)
            _camera = GetComponent<Camera>();

        if (renderingCamera != _camera)
            return;

        if (_capturePending)
        {
            _capturePending = false;
            CaptureCurrentDirection();
        }

        PushGlobals();
    }

    [ContextMenu("Capture Current Focus Frame")]
    public void CaptureCurrentDirection()
    {
        if (_camera == null)
            _camera = GetComponent<Camera>();

        Transform cameraTransform = _camera != null ? _camera.transform : transform;
        Vector3 forward = cameraTransform.forward;
        Vector3 right = cameraTransform.right;
        Vector3 up = cameraTransform.up;
        if (forward.sqrMagnitude < 1e-6f)
            forward = Vector3.forward;
        if (right.sqrMagnitude < 1e-6f)
            right = Vector3.right;
        if (up.sqrMagnitude < 1e-6f)
            up = Vector3.up;

        _capturedOrigin = cameraTransform.position;
        _capturedForward = forward.normalized;
        _capturedRight = right.normalized;
        _capturedUp = up.normalized;
        _capturedAspect = _camera != null ? Mathf.Max(0.01f, _camera.aspect) : customFocusAspect;
        _hasCapturedForward = true;
    }

    public bool TryEvaluateFocus(Camera viewerCamera, out FocusEvaluation evaluation)
    {
        evaluation = default(FocusEvaluation);

        if (viewerCamera == null)
        {
            if (_camera == null)
                _camera = GetComponent<Camera>();
            viewerCamera = _camera;
        }

        if (viewerCamera == null)
            return false;

        Transform cameraTransform = viewerCamera.transform;
        return TryEvaluateFocus(
            cameraTransform.position,
            cameraTransform.forward,
            cameraTransform.right,
            cameraTransform.up,
            Mathf.Max(0.01f, viewerCamera.aspect),
            out evaluation);
    }

    public bool TryEvaluateFocus(
        Vector3 cameraPosition,
        Vector3 gazeDirection,
        Vector3 cameraRight,
        Vector3 cameraUp,
        float cameraAspect,
        out FocusEvaluation evaluation)
    {
        evaluation = default(FocusEvaluation);

        if (gazeDirection.sqrMagnitude < 1e-6f)
            return false;

        Vector3 cameraForward = gazeDirection.normalized;
        cameraRight = NormalizeOrFallback(cameraRight, Vector3.right);
        cameraUp = NormalizeOrFallback(cameraUp, Vector3.up);

        bool useCapturedFrame = lockToCapturedDirection && _hasCapturedForward;
        Vector3 focusOrigin = useCapturedFrame ? _capturedOrigin : cameraPosition;
        Vector3 focusForward = NormalizeOrFallback(useCapturedFrame ? _capturedForward : cameraForward, Vector3.forward);
        Vector3 focusRight = NormalizeOrFallback(useCapturedFrame ? _capturedRight : cameraRight, Vector3.right);
        Vector3 focusUp = NormalizeOrFallback(useCapturedFrame ? _capturedUp : cameraUp, Vector3.up);
        float focusAspect = useCameraAspectForFocus
            ? (useCapturedFrame ? _capturedAspect : Mathf.Max(0.01f, cameraAspect))
            : Mathf.Max(0.01f, customFocusAspect);
        float innerTanY = Mathf.Tan(Mathf.Deg2Rad * Mathf.Clamp(innerAngle, 0.1f, 89f));
        float outerTanY = Mathf.Tan(Mathf.Deg2Rad * Mathf.Clamp(Mathf.Max(innerAngle + 0.1f, outerAngle), 0.1f, 89f));
        float innerTanX = innerTanY * focusAspect;
        float outerTanX = outerTanY * focusAspect;
        Vector3 focusVector = cameraForward;

        evaluation.WorldAnchored = worldAnchoredFocus;
        evaluation.UsesCapturedFrame = useCapturedFrame;

        if (frontHemisphereOnly)
        {
            evaluation.FocusT = Vector3.Dot(cameraForward, focusForward) >= 0f ? 0f : 1f;
            evaluation.HorizontalAngle = Mathf.Atan2(Vector3.Dot(cameraForward, focusRight), Vector3.Dot(cameraForward, focusForward)) * Mathf.Rad2Deg;
            evaluation.VerticalAngle = Mathf.Atan2(Vector3.Dot(cameraForward, focusUp), Vector3.Dot(cameraForward, focusForward)) * Mathf.Rad2Deg;
            evaluation.AngularDistance = Vector3.Angle(focusForward, cameraForward);
            return true;
        }

        if (worldAnchoredFocus)
        {
            Vector3 focusPlane = focusOrigin + focusForward * Mathf.Max(0.1f, focusPlaneDistance);
            float denom = Vector3.Dot(cameraForward, focusForward);

            if (Mathf.Abs(denom) < 1e-5f)
            {
                evaluation.FocusT = 1f;
                return true;
            }

            float rayDistance = Vector3.Dot(focusPlane - cameraPosition, focusForward) / denom;
            if (rayDistance <= 0f)
            {
                evaluation.FocusT = 1f;
                return true;
            }

            Vector3 pointOnFocusPlane = cameraPosition + cameraForward * rayDistance;
            focusVector = pointOnFocusPlane - focusOrigin;
        }

        float focusDepth = Vector3.Dot(focusVector, focusForward);
        if (focusDepth <= 1e-5f)
        {
            evaluation.FocusT = 1f;
            return true;
        }

        float signedXSlope = Vector3.Dot(focusVector, focusRight) / focusDepth;
        float signedYSlope = Vector3.Dot(focusVector, focusUp) / focusDepth;
        float xMask = SmoothStep(innerTanX, outerTanX, Mathf.Abs(signedXSlope));
        float yMask = SmoothStep(innerTanY, outerTanY, Mathf.Abs(signedYSlope));

        evaluation.FocusT = Mathf.Clamp01(Mathf.Max(xMask, yMask));
        evaluation.SignedHorizontalSlope = signedXSlope;
        evaluation.SignedVerticalSlope = signedYSlope;
        evaluation.HorizontalAngle = Mathf.Atan(signedXSlope) * Mathf.Rad2Deg;
        evaluation.VerticalAngle = Mathf.Atan(signedYSlope) * Mathf.Rad2Deg;
        evaluation.AngularDistance = Vector3.Angle(focusForward, focusVector);
        return true;
    }

    void PushGlobals()
    {
        if (!effectEnabled)
        {
            Shader.SetGlobalFloat(s_EffectEnabledId, 0f);
            return;
        }

        if (_camera == null)
            _camera = GetComponent<Camera>();

        if (_camera == null)
        {
            Shader.SetGlobalFloat(s_EffectEnabledId, 0f);
            return;
        }

        if (!_hasCapturedForward && !_capturePending)
            CaptureCurrentDirection();

        Transform cameraTransform = _camera.transform;
        Vector3 cameraPosition = cameraTransform.position;
        Vector3 cameraForward = cameraTransform.forward.normalized;
        Vector3 cameraRight = cameraTransform.right.normalized;
        Vector3 cameraUp = cameraTransform.up.normalized;
        bool useCapturedFrame = lockToCapturedDirection && _hasCapturedForward;
        Vector3 focusOrigin = useCapturedFrame ? _capturedOrigin : cameraPosition;
        Vector3 focusForward = useCapturedFrame ? _capturedForward : cameraForward;
        Vector3 focusRight = useCapturedFrame ? _capturedRight : cameraRight;
        Vector3 focusUp = useCapturedFrame ? _capturedUp : cameraUp;
        float focusAspect = useCameraAspectForFocus
            ? (useCapturedFrame ? _capturedAspect : Mathf.Max(0.01f, _camera.aspect))
            : Mathf.Max(0.01f, customFocusAspect);
        float innerTanY = Mathf.Tan(Mathf.Deg2Rad * Mathf.Clamp(innerAngle, 0.1f, 89f));
        float outerTanY = Mathf.Tan(Mathf.Deg2Rad * Mathf.Clamp(Mathf.Max(innerAngle + 0.1f, outerAngle), 0.1f, 89f));
        float innerTanX = innerTanY * focusAspect;
        float outerTanX = outerTanY * focusAspect;
        float tanHalfFovY = Mathf.Tan(0.5f * Mathf.Deg2Rad * Mathf.Clamp(_camera.fieldOfView, 1f, 179f));
        Cubemap activeSkybox = ResolveSkyboxCubemap();

        Shader.SetGlobalFloat(s_EffectEnabledId, 1f);
        Shader.SetGlobalVector(s_FocusForwardId, new Vector4(focusForward.x, focusForward.y, focusForward.z, 0f));
        Shader.SetGlobalVector(s_FocusRightId, new Vector4(focusRight.x, focusRight.y, focusRight.z, 0f));
        Shader.SetGlobalVector(s_FocusUpId, new Vector4(focusUp.x, focusUp.y, focusUp.z, 0f));
        Shader.SetGlobalVector(s_FocusOriginId, new Vector4(focusOrigin.x, focusOrigin.y, focusOrigin.z, 1f));
        Shader.SetGlobalVector(s_CameraPositionId, new Vector4(cameraPosition.x, cameraPosition.y, cameraPosition.z, 1f));
        Shader.SetGlobalVector(s_CameraForwardId, new Vector4(cameraForward.x, cameraForward.y, cameraForward.z, 0f));
        Shader.SetGlobalVector(s_CameraRightId, new Vector4(cameraRight.x, cameraRight.y, cameraRight.z, 0f));
        Shader.SetGlobalVector(s_CameraUpId, new Vector4(cameraUp.x, cameraUp.y, cameraUp.z, 0f));
        Shader.SetGlobalVector(s_FocusParamsId, new Vector4(
            innerTanX,
            innerTanY,
            Mathf.Max(0f, maxBlurPixels),
            Mathf.Clamp01(peripheralDim)));
        Shader.SetGlobalVector(s_CameraParamsId, new Vector4(
            tanHalfFovY,
            Mathf.Max(0.01f, _camera.aspect),
            0f,
            0f));
        Shader.SetGlobalVector(s_RectParamsId, new Vector4(
            outerTanX,
            outerTanY,
            Mathf.Max(0.1f, focusPlaneDistance),
            worldAnchoredFocus ? 1f : 0f));
        Shader.SetGlobalVector(s_DisplayParamsId, new Vector4(
            replacePeripheralWithSkybox ? 1f : 0f,
            activeSkybox != null ? 1f : 0f,
            Mathf.Max(0f, skyboxExposure),
            frontHemisphereOnly ? 1f : 0f));
        if (activeSkybox != null)
            Shader.SetGlobalTexture(s_SkyboxCubemapId, activeSkybox);
        Shader.SetGlobalColor(s_SkyboxZenithId, skyboxZenithColor);
        Shader.SetGlobalColor(s_SkyboxHorizonId, skyboxHorizonColor);
        Shader.SetGlobalColor(s_SkyboxGroundId, skyboxGroundColor);
    }

    Cubemap ResolveSkyboxCubemap()
    {
        if (skyboxCubemap != null)
            return skyboxCubemap;

        if (!useRenderSettingsSkybox || RenderSettings.skybox == null)
            return null;

        Material skybox = RenderSettings.skybox;
        foreach (string propertyName in s_SkyboxTexturePropertyNames)
        {
            if (!skybox.HasProperty(propertyName))
                continue;

            if (skybox.GetTexture(propertyName) is Cubemap cubemap)
                return cubemap;
        }

        return null;
    }

    static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
    {
        return value.sqrMagnitude > 1e-6f ? value.normalized : fallback;
    }

    static float SmoothStep(float edge0, float edge1, float value)
    {
        if (edge1 <= edge0)
            return value >= edge1 ? 1f : 0f;

        float t = Mathf.Clamp01((value - edge0) / (edge1 - edge0));
        return t * t * (3f - 2f * t);
    }
}
