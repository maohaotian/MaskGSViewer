// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Runtime importer/player for camera trajectories.
///
/// Supported JSON shapes:
/// - {"frames":[{"transform_matrix":[[...],[...],[...],[...]]}]}
/// - {"camera_path":[{"camera_to_world":[... 16 numbers ...]}]}
/// - {"frames":[{"position":[x,y,z],"rotation":[x,y,z,w]}]}
/// - [{"position":[x,y,z],"euler":[x,y,z]}]
///
/// The trajectory is deliberately separate from splat importing. It only moves
/// the viewer rig, so new splat formats can evolve independently.
/// </summary>
[DefaultExecutionOrder(-850)]
public sealed class CameraTrajectoryPlayer : MonoBehaviour
{
    public enum MatrixForwardConvention
    {
        UnityPositiveZ,
        OpenGlNegativeZ
    }

    public enum QuaternionComponentOrder
    {
        XYZW,
        WXYZ
    }

    [Header("References")]
    [Tooltip("Viewer rig to move. Auto-resolved if empty.")]
    public VRRig vrRig;

    [Header("Loading")]
    [Tooltip("Optional trajectory JSON path. Relative paths are searched under Assets/GaussianAssets first.")]
    public string trajectoryPath = "";

    [Tooltip("Load trajectoryPath automatically on Start.")]
    public bool autoLoadOnStart = false;

    [Tooltip("Apply the first frame immediately after a trajectory is loaded.")]
    public bool applyFirstFrameOnLoad = true;

    [Header("Coordinate Conversion")]
    [Tooltip("Scale applied to imported camera positions.")]
    public float positionScale = 1f;

    [Tooltip("Root offset applied after scale/axis flips.")]
    public Vector3 rootPosition = Vector3.zero;

    [Tooltip("Root rotation applied after scale/axis flips.")]
    public Vector3 rootEuler = Vector3.zero;

    [Tooltip("Flip imported X coordinates/directions before root transform.")]
    public bool flipX = false;

    [Tooltip("Flip imported Y coordinates/directions before root transform.")]
    public bool flipY = false;

    [Tooltip("Flip imported Z coordinates/directions before root transform.")]
    public bool flipZ = false;

    [Header("Matrix Parsing")]
    [Tooltip("Use OpenGlNegativeZ for NeRF/Blender camera-to-world matrices where the camera looks along -Z.")]
    public MatrixForwardConvention matrixForward = MatrixForwardConvention.UnityPositiveZ;

    [Tooltip("Transpose imported 4x4 matrices before reading pose. Useful for column-major flat arrays.")]
    public bool transposeMatrices = false;

    [Header("Rotation Parsing")]
    [Tooltip("Quaternion array order for position/rotation JSON frames.")]
    public QuaternionComponentOrder quaternionOrder = QuaternionComponentOrder.XYZW;

    [Tooltip("Treat 3-number euler arrays as radians instead of degrees.")]
    public bool eulerAnglesAreRadians = false;

    [Header("Playback")]
    [Tooltip("Playback speed when T is pressed on desktop.")]
    [Min(0.01f)] public float playbackFps = 1f;

    [Tooltip("Loop when playback reaches the end.")]
    public bool loopPlayback = true;

    [Tooltip("Clamp desktop pitch after applying imported views, matching FPS-style mouse look.")]
    [Range(1f, 89.9f)] public float maxDesktopPitch = 89f;

    [Header("VR Input")]
    [Tooltip("Grip amount required for trajectory shortcuts.")]
    [Range(0.1f, 1f)] public float gripThreshold = 0.5f;

    [Tooltip("Trigger amount required for trajectory previous/next shortcuts.")]
    [Range(0.1f, 1f)] public float triggerThreshold = 0.5f;

    [Header("Status (read-only)")]
    [SerializeField] string _currentTrajectory = "(none)";
    [SerializeField] int _currentFrame = -1;
    [SerializeField] int _frameCount;
    [SerializeField] bool _isPlaying;

    readonly List<CameraPose> _frames = new List<CameraPose>();
    string _currentTrajectoryPath;
    VRFileBrowser _browser;
    float _playbackTimer;
    bool _desktopToggleReady = true;
    bool _vrNextReady = true;
    bool _vrPreviousReady = true;

    struct CameraPose
    {
        public Vector3 position;
        public Quaternion rotation;
        public float fovY;
        public float time;
        public string label;
    }

    public string CurrentTrajectoryPath => _currentTrajectoryPath;
    public int CurrentFrame => _currentFrame;
    public int FrameCount => _frameCount;
    public bool IsPlaying => _isPlaying;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        FindOrCreate();
    }

    public static CameraTrajectoryPlayer FindOrCreate()
    {
        var existing = FindAnyObjectByType<CameraTrajectoryPlayer>();
        if (existing != null)
            return existing;

        var go = new GameObject(nameof(CameraTrajectoryPlayer));
        DontDestroyOnLoad(go);
        return go.AddComponent<CameraTrajectoryPlayer>();
    }

    public static bool IsSupportedFileExtension(string filePath)
    {
        return string.Equals(Path.GetExtension(filePath), ".json", StringComparison.OrdinalIgnoreCase);
    }

    public static string FindMatchingTrajectoryPath(string splatFilePath)
    {
        if (string.IsNullOrWhiteSpace(splatFilePath))
            return null;

        string folder = Path.GetDirectoryName(splatFilePath);
        if (string.IsNullOrWhiteSpace(folder))
            return null;

        string fileName = Path.GetFileNameWithoutExtension(splatFilePath);
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        string[] candidates =
        {
            Path.Combine(folder, fileName + ".json"),
            Path.Combine(folder, "camera_trajectory_unity_dx12.json"),
            Path.Combine(folder, "camera_trajectory.json")
        };

        for (int i = 0; i < candidates.Length; i++)
        {
            if (File.Exists(candidates[i]))
                return candidates[i];
        }

        if (!Directory.Exists(folder))
            return null;

        string[] trajectoryFiles = Directory.GetFiles(folder, "camera_trajectory*.json");
        return trajectoryFiles.Length == 1 ? trajectoryFiles[0] : null;
    }

    void Awake()
    {
        ResolveReferences();
    }

    void Start()
    {
        ResolveReferences();

        if (autoLoadOnStart && !string.IsNullOrWhiteSpace(trajectoryPath))
            LoadTrajectory(trajectoryPath, applyFirstFrameOnLoad);
    }

    void Update()
    {
        if (_frames.Count == 0)
            return;

        if (_browser != null && (_browser.IsOpen || _browser.WasOpenThisFrame))
            return;

        if (XRSettings.isDeviceActive)
            HandleVRInput();
        HandleDesktopInput();

        if (_isPlaying)
            PumpPlayback();
    }

    [ContextMenu("Load Trajectory Path")]
    public void LoadTrajectoryPath()
    {
        LoadTrajectory(trajectoryPath, applyFirstFrameOnLoad);
    }

    public bool LoadTrajectory(string filePath, bool applyFirstFrame)
    {
        string resolvedPath = ResolveTrajectoryPath(filePath);
        if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
        {
            Debug.LogWarning($"[CameraTrajectoryPlayer] Trajectory file not found: {filePath}");
            return false;
        }

        if (!IsSupportedFileExtension(resolvedPath))
        {
            Debug.LogWarning($"[CameraTrajectoryPlayer] Unsupported trajectory extension: {Path.GetExtension(resolvedPath)}");
            return false;
        }

        try
        {
            var parsedFrames = ParseTrajectoryJson(File.ReadAllText(resolvedPath));
            if (parsedFrames.Count == 0)
            {
                Debug.LogWarning($"[CameraTrajectoryPlayer] No camera frames found in {resolvedPath}");
                return false;
            }

            _frames.Clear();
            _frames.AddRange(parsedFrames);
            _currentTrajectoryPath = resolvedPath;
            _currentTrajectory = Path.GetFileName(resolvedPath);
            _frameCount = _frames.Count;
            _currentFrame = -1;
            trajectoryPath = resolvedPath;
            _isPlaying = false;
            _playbackTimer = 0f;

            if (applyFirstFrame)
                ApplyFrame(0, true);

            Debug.Log($"[CameraTrajectoryPlayer] Loaded {_frameCount} camera frame(s): {resolvedPath}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[CameraTrajectoryPlayer] Failed to load {resolvedPath}: {ex.Message}");
            return false;
        }
    }

    public bool TryLoadMatchingTrajectoryForSplat(string splatFilePath, bool applyFirstFrame, bool clearIfMissing = true)
    {
        string matchingPath = FindMatchingTrajectoryPath(splatFilePath);
        if (string.IsNullOrWhiteSpace(matchingPath))
        {
            if (clearIfMissing)
                ClearTrajectory();
            return false;
        }

        bool loaded = LoadTrajectory(matchingPath, applyFirstFrame);
        if (!loaded && clearIfMissing)
            ClearTrajectory();
        return loaded;
    }

    public void ClearTrajectory()
    {
        if (_frames.Count == 0 && string.IsNullOrEmpty(_currentTrajectoryPath))
            return;

        _frames.Clear();
        _currentTrajectoryPath = null;
        _currentTrajectory = "(none)";
        _frameCount = 0;
        _currentFrame = -1;
        _isPlaying = false;
        _playbackTimer = 0f;
    }

    [ContextMenu("Apply First Frame")]
    public void ApplyFirstFrame()
    {
        ApplyFrame(0, true);
    }

    public void ApplyFrame(int index, bool logFrame)
    {
        if (_frames.Count == 0)
            return;

        index = Mathf.Clamp(index, 0, _frames.Count - 1);
        CameraPose pose = ConvertPose(_frames[index]);
        ApplyPoseToViewer(pose.position, pose.rotation, pose.fovY);

        _currentFrame = index;
        if (logFrame)
        {
            string label = string.IsNullOrEmpty(_frames[index].label) ? index.ToString(CultureInfo.InvariantCulture) : _frames[index].label;
            Debug.Log($"[CameraTrajectoryPlayer] Applied frame {index + 1}/{_frames.Count}: {label}");
        }
    }

    public void NextFrame()
    {
        if (_frames.Count == 0)
            return;

        int next = _currentFrame < 0 ? 0 : (_currentFrame + 1) % _frames.Count;
        ApplyFrame(next, true);
    }

    public void PreviousFrame()
    {
        if (_frames.Count == 0)
            return;

        int previous = _currentFrame < 0 ? 0 : (_currentFrame - 1 + _frames.Count) % _frames.Count;
        ApplyFrame(previous, true);
    }

    public void TogglePlayback()
    {
        if (_frames.Count == 0)
            return;

        _isPlaying = !_isPlaying;
        _playbackTimer = 0f;
        Debug.Log($"[CameraTrajectoryPlayer] Playback {(_isPlaying ? "started" : "stopped")}");
    }

    void ResolveReferences()
    {
        if (vrRig == null)
            vrRig = FindAnyObjectByType<VRRig>();
        if (_browser == null)
            _browser = FindAnyObjectByType<VRFileBrowser>();
    }

    string ResolveTrajectoryPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        if (Path.IsPathRooted(filePath))
            return filePath;

        string gaussianAssetsPath = Path.Combine(Application.dataPath, "GaussianAssets", filePath);
        if (File.Exists(gaussianAssetsPath))
            return gaussianAssetsPath;

        string dataPath = Path.Combine(Application.dataPath, filePath);
        if (File.Exists(dataPath))
            return dataPath;

        return filePath;
    }

    void HandleDesktopInput()
    {
        ReadDesktopShortcuts(out bool previous, out bool next, out bool togglePlayback);

        if (next)
            NextFrame();
        if (previous)
            PreviousFrame();

        if (togglePlayback && _desktopToggleReady)
        {
            TogglePlayback();
            _desktopToggleReady = false;
        }
        else if (!togglePlayback)
        {
            _desktopToggleReady = true;
        }
    }

    static void ReadDesktopShortcuts(out bool previous, out bool next, out bool togglePlayback)
    {
        previous = Input.GetKeyDown(KeyCode.LeftBracket);
        next = Input.GetKeyDown(KeyCode.RightBracket);
        togglePlayback = Input.GetKey(KeyCode.T);

#if ENABLE_INPUT_SYSTEM
        var keyboard = UnityEngine.InputSystem.Keyboard.current;
        if (keyboard != null)
        {
            previous |= keyboard.leftBracketKey.wasPressedThisFrame;
            next |= keyboard.rightBracketKey.wasPressedThisFrame;
            togglePlayback |= keyboard.tKey.isPressed;
        }
#endif

        string typed = Input.inputString;
        for (int i = 0; i < typed.Length; i++)
        {
            char c = typed[i];
            if (c == '[' || c == '【' || c == '［')
                previous = true;
            else if (c == ']' || c == '】' || c == '］')
                next = true;
            else if (c == 't' || c == 'T')
                togglePlayback = true;
        }
    }

    void HandleVRInput()
    {
        bool modifier = ReadAxis1D(XRNode.LeftHand, CommonUsages.grip) >= gripThreshold;
        if (!modifier)
        {
            _vrNextReady = true;
            _vrPreviousReady = true;
            return;
        }

        bool next = ReadAxis1D(XRNode.RightHand, CommonUsages.trigger) >= triggerThreshold;
        bool previous = ReadAxis1D(XRNode.LeftHand, CommonUsages.trigger) >= triggerThreshold;

        if (next && _vrNextReady)
        {
            NextFrame();
            _vrNextReady = false;
        }
        else if (!next)
        {
            _vrNextReady = true;
        }

        if (previous && _vrPreviousReady)
        {
            PreviousFrame();
            _vrPreviousReady = false;
        }
        else if (!previous)
        {
            _vrPreviousReady = true;
        }
    }

    void PumpPlayback()
    {
        float interval = 1f / Mathf.Max(0.01f, playbackFps);
        _playbackTimer += Time.deltaTime;
        if (_playbackTimer < interval)
            return;

        _playbackTimer -= interval;
        int next = _currentFrame < 0 ? 0 : _currentFrame + 1;
        if (next >= _frames.Count)
        {
            if (!loopPlayback)
            {
                _isPlaying = false;
                return;
            }
            next = 0;
        }
        ApplyFrame(next, false);
    }

    CameraPose ConvertPose(CameraPose raw)
    {
        Vector3 forward = ConvertDirection(raw.rotation * Vector3.forward);
        Vector3 up = ConvertDirection(raw.rotation * Vector3.up);

        return new CameraPose
        {
            position = ConvertPoint(raw.position),
            rotation = SafeLookRotation(forward, up),
            fovY = raw.fovY,
            time = raw.time,
            label = raw.label
        };
    }

    Vector3 ConvertPoint(Vector3 point)
    {
        point = ApplyAxisFlips(point) * positionScale;
        return rootPosition + Quaternion.Euler(rootEuler) * point;
    }

    Vector3 ConvertDirection(Vector3 direction)
    {
        direction = ApplyAxisFlips(direction);
        return Quaternion.Euler(rootEuler) * direction;
    }

    Vector3 ApplyAxisFlips(Vector3 value)
    {
        if (flipX) value.x = -value.x;
        if (flipY) value.y = -value.y;
        if (flipZ) value.z = -value.z;
        return value;
    }

    void ApplyPoseToViewer(Vector3 eyePosition, Quaternion eyeRotation, float fovY)
    {
        ResolveReferences();

        if (vrRig == null)
        {
            Camera fallbackCamera = Camera.main;
            if (fallbackCamera != null)
            {
                ApplyCameraFov(fallbackCamera, fovY);
                fallbackCamera.transform.SetPositionAndRotation(eyePosition, eyeRotation);
            }
            return;
        }

        Transform rigTransform = vrRig.transform;
        Camera camera = vrRig.xrCamera != null ? vrRig.xrCamera : Camera.main;

        Vector3 localEyeOffset = camera != null
            ? rigTransform.InverseTransformPoint(camera.transform.position)
            : Vector3.up * vrRig.eyeHeight;

        Vector3 forward = eyeRotation * Vector3.forward;
        Quaternion rigYaw = BuildYawRotation(forward, rigTransform.rotation);

        rigTransform.rotation = rigYaw;
        rigTransform.position = eyePosition - rigYaw * localEyeOffset;

        if (!XRSettings.isDeviceActive && camera != null)
            ApplyDesktopCameraPose(camera, rigYaw, eyeRotation);

        ApplyCameraFov(camera, fovY);

        var locomotion = vrRig.GetComponent<VRLocomotion>();
        if (locomotion != null)
            locomotion.SyncDesktopLookFromCurrentPose();
    }

    static void ApplyCameraFov(Camera camera, float fovY)
    {
        if (camera != null && fovY > 0.01f && fovY < 179.9f)
            camera.fieldOfView = fovY;
    }

    static void ApplyDesktopCameraPose(Camera camera, Quaternion rigYaw, Quaternion eyeRotation)
    {
        camera.transform.localRotation = Quaternion.Inverse(rigYaw) * eyeRotation;
    }

    static Quaternion BuildYawRotation(Vector3 forward, Quaternion fallback)
    {
        Vector3 flat = forward;
        flat.y = 0f;
        if (flat.sqrMagnitude < 0.0001f)
            return Quaternion.Euler(0f, fallback.eulerAngles.y, 0f);

        return Quaternion.LookRotation(flat.normalized, Vector3.up);
    }

    static Quaternion SafeLookRotation(Vector3 forward, Vector3 up)
    {
        if (forward.sqrMagnitude < 0.0001f)
            forward = Vector3.forward;
        if (up.sqrMagnitude < 0.0001f || Vector3.Cross(forward, up).sqrMagnitude < 0.0001f)
            up = Vector3.up;

        return Quaternion.LookRotation(forward.normalized, up.normalized);
    }

    static float NormalizeAngle(float angle)
    {
        return Mathf.Repeat(angle + 180f, 360f) - 180f;
    }

    List<CameraPose> ParseTrajectoryJson(string json)
    {
        object root = MiniJson.Parse(json);
        var result = new List<CameraPose>();

        if (root is List<object> rootList)
        {
            ReadFrameList(rootList, result);
            return result;
        }

        if (root is Dictionary<string, object> rootDict)
        {
            if (TryGetList(rootDict, "frames", out var frames))
                ReadFrameList(frames, result);
            if (result.Count == 0 && TryGetList(rootDict, "camera_path", out var cameraPath))
                ReadFrameList(cameraPath, result);
            if (result.Count == 0 && TryGetList(rootDict, "keyframes", out var keyframes))
                ReadFrameList(keyframes, result);
            if (result.Count == 0 && TryGetList(rootDict, "cameras", out var cameras))
                ReadFrameList(cameras, result);
            if (result.Count == 0 && TryReadPose(rootDict, 0, out var singlePose))
                result.Add(singlePose);
        }

        return result;
    }

    void ReadFrameList(List<object> frames, List<CameraPose> result)
    {
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i] is Dictionary<string, object> frameDict && TryReadPose(frameDict, i, out var pose))
                result.Add(pose);
        }
    }

    bool TryReadPose(Dictionary<string, object> frame, int index, out CameraPose pose)
    {
        if (TryReadPosition(frame, out Vector3 position))
        {
            Quaternion rotation = Quaternion.identity;
            if (!TryReadRotation(frame, out rotation) && TryReadLookAt(frame, position, out Quaternion lookRotation))
                rotation = lookRotation;

            pose = new CameraPose
            {
                position = position,
                rotation = rotation,
                fovY = ReadOptionalFovY(frame),
                time = ReadOptionalFloat(frame, "time", index),
                label = ReadOptionalLabel(frame, index)
            };
            return true;
        }

        foreach (string key in new[] { "transform_matrix", "camera_to_world", "c2w", "matrix", "transform" })
        {
            if (frame.TryGetValue(key, out object value) && TryReadMatrix(value, out Matrix4x4 matrix))
            {
                pose = PoseFromMatrix(matrix, frame, index);
                return true;
            }
        }

        foreach (string key in new[] { "world_to_camera", "w2c", "view_matrix" })
        {
            if (frame.TryGetValue(key, out object value) && TryReadMatrix(value, out Matrix4x4 matrix))
            {
                pose = PoseFromMatrix(matrix.inverse, frame, index);
                return true;
            }
        }

        if (TryGetDict(frame, "camera", out var camera) && TryReadPose(camera, index, out pose))
            return true;
        if (TryGetDict(frame, "pose", out var nestedPose) && TryReadPose(nestedPose, index, out pose))
            return true;

        pose = default;
        return false;
    }

    CameraPose PoseFromMatrix(Matrix4x4 matrix, Dictionary<string, object> frame, int index)
    {
        if (transposeMatrices)
            matrix = matrix.transpose;

        Vector3 position = new Vector3(matrix.m03, matrix.m13, matrix.m23);
        Vector3 up = new Vector3(matrix.m01, matrix.m11, matrix.m21);
        Vector3 forward = new Vector3(matrix.m02, matrix.m12, matrix.m22);
        if (matrixForward == MatrixForwardConvention.OpenGlNegativeZ)
            forward = -forward;

        return new CameraPose
        {
            position = position,
            rotation = SafeLookRotation(forward, up),
            fovY = ReadOptionalFovY(frame),
            time = ReadOptionalFloat(frame, "time", index),
            label = ReadOptionalLabel(frame, index)
        };
    }

    bool TryReadMatrix(object value, out Matrix4x4 matrix)
    {
        matrix = Matrix4x4.identity;
        if (!(value is List<object> list))
            return false;

        if (list.Count >= 4 && list[0] is List<object>)
        {
            for (int row = 0; row < 4; row++)
            {
                if (!(list[row] is List<object> rowList) || rowList.Count < 4)
                    return false;

                for (int col = 0; col < 4; col++)
                {
                    if (!TryToFloat(rowList[col], out float v))
                        return false;
                    matrix[row, col] = v;
                }
            }
            return true;
        }

        if (list.Count >= 16)
        {
            for (int row = 0; row < 4; row++)
            {
                for (int col = 0; col < 4; col++)
                {
                    if (!TryToFloat(list[row * 4 + col], out float v))
                        return false;
                    matrix[row, col] = v;
                }
            }
            return true;
        }

        return false;
    }

    bool TryReadPosition(Dictionary<string, object> frame, out Vector3 position)
    {
        foreach (string key in new[] { "position", "pos", "translation", "origin" })
        {
            if (frame.TryGetValue(key, out object value) && TryReadVector3(value, out position))
                return true;
        }

        position = default;
        return false;
    }

    bool TryReadRotation(Dictionary<string, object> frame, out Quaternion rotation)
    {
        foreach (string key in new[] { "rotation", "quaternion", "quat", "rotation_quaternion" })
        {
            if (!frame.TryGetValue(key, out object value))
                continue;

            if (TryReadQuaternion(value, out rotation))
                return true;
            if (TryReadEuler(value, out rotation))
                return true;
        }

        foreach (string key in new[] { "euler", "rotation_euler", "euler_angles" })
        {
            if (frame.TryGetValue(key, out object value) && TryReadEuler(value, out rotation))
                return true;
        }

        rotation = Quaternion.identity;
        return false;
    }

    bool TryReadLookAt(Dictionary<string, object> frame, Vector3 position, out Quaternion rotation)
    {
        foreach (string key in new[] { "look_at", "target", "center" })
        {
            if (frame.TryGetValue(key, out object value) && TryReadVector3(value, out Vector3 target))
            {
                Vector3 forward = target - position;
                rotation = SafeLookRotation(forward, Vector3.up);
                return true;
            }
        }

        rotation = Quaternion.identity;
        return false;
    }

    bool TryReadVector3(object value, out Vector3 vector)
    {
        if (value is List<object> list && list.Count >= 3
            && TryToFloat(list[0], out float x)
            && TryToFloat(list[1], out float y)
            && TryToFloat(list[2], out float z))
        {
            vector = new Vector3(x, y, z);
            return true;
        }

        if (value is Dictionary<string, object> dict
            && TryReadNamedFloat(dict, "x", out float dx)
            && TryReadNamedFloat(dict, "y", out float dy)
            && TryReadNamedFloat(dict, "z", out float dz))
        {
            vector = new Vector3(dx, dy, dz);
            return true;
        }

        vector = default;
        return false;
    }

    bool TryReadQuaternion(object value, out Quaternion rotation)
    {
        if (value is List<object> list && list.Count >= 4
            && TryToFloat(list[0], out float a)
            && TryToFloat(list[1], out float b)
            && TryToFloat(list[2], out float c)
            && TryToFloat(list[3], out float d))
        {
            rotation = quaternionOrder == QuaternionComponentOrder.XYZW
                ? new Quaternion(a, b, c, d)
                : new Quaternion(b, c, d, a);
            return true;
        }

        if (value is Dictionary<string, object> dict
            && TryReadNamedFloat(dict, "x", out float x)
            && TryReadNamedFloat(dict, "y", out float y)
            && TryReadNamedFloat(dict, "z", out float z)
            && TryReadNamedFloat(dict, "w", out float w))
        {
            rotation = new Quaternion(x, y, z, w);
            return true;
        }

        rotation = Quaternion.identity;
        return false;
    }

    bool TryReadEuler(object value, out Quaternion rotation)
    {
        if (TryReadVector3(value, out Vector3 euler))
        {
            if (eulerAnglesAreRadians)
                euler *= Mathf.Rad2Deg;
            rotation = Quaternion.Euler(euler);
            return true;
        }

        rotation = Quaternion.identity;
        return false;
    }

    static bool TryGetList(Dictionary<string, object> dict, string key, out List<object> list)
    {
        if (dict.TryGetValue(key, out object value) && value is List<object> typed)
        {
            list = typed;
            return true;
        }

        list = null;
        return false;
    }

    static bool TryGetDict(Dictionary<string, object> dict, string key, out Dictionary<string, object> nested)
    {
        if (dict.TryGetValue(key, out object value) && value is Dictionary<string, object> typed)
        {
            nested = typed;
            return true;
        }

        nested = null;
        return false;
    }

    static float ReadOptionalFloat(Dictionary<string, object> dict, string key, float fallback)
    {
        return TryReadNamedFloat(dict, key, out float value) ? value : fallback;
    }

    static float ReadOptionalFovY(Dictionary<string, object> dict)
    {
        foreach (string key in new[] { "fov_y", "fovY", "vertical_fov", "fieldOfView" })
        {
            if (TryReadNamedFloat(dict, key, out float value))
                return value;
        }

        return 0f;
    }

    static string ReadOptionalLabel(Dictionary<string, object> dict, int fallbackIndex)
    {
        foreach (string key in new[] { "file_path", "name", "label", "id" })
        {
            if (dict.TryGetValue(key, out object value) && value != null)
                return value.ToString();
        }

        return fallbackIndex.ToString(CultureInfo.InvariantCulture);
    }

    static bool TryReadNamedFloat(Dictionary<string, object> dict, string key, out float value)
    {
        if (dict.TryGetValue(key, out object raw))
            return TryToFloat(raw, out value);

        value = 0f;
        return false;
    }

    static bool TryToFloat(object value, out float result)
    {
        switch (value)
        {
            case float f:
                result = f;
                return true;
            case double d:
                result = (float)d;
                return true;
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            case string s when float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed):
                result = parsed;
                return true;
            default:
                result = 0f;
                return false;
        }
    }

    static readonly List<InputDevice> s_devices = new List<InputDevice>(2);

    static float ReadAxis1D(XRNode node, InputFeatureUsage<float> usage)
    {
        s_devices.Clear();
        InputDevices.GetDevicesAtXRNode(node, s_devices);
        if (s_devices.Count > 0 && s_devices[0].TryGetFeatureValue(usage, out float value))
            return value;

        return 0f;
    }

    static class MiniJson
    {
        public static object Parse(string json)
        {
            if (json == null)
                throw new ArgumentNullException(nameof(json));

            var parser = new Parser(json);
            object value = parser.ParseValue();
            parser.SkipWhitespace();
            if (!parser.IsEnd)
                throw new FormatException("Unexpected trailing JSON content.");
            return value;
        }

        sealed class Parser
        {
            readonly string _json;
            int _index;

            public Parser(string json)
            {
                _json = json;
            }

            public bool IsEnd => _index >= _json.Length;

            public object ParseValue()
            {
                SkipWhitespace();
                if (IsEnd)
                    throw new FormatException("Unexpected end of JSON.");

                char c = _json[_index];
                if (c == '{') return ParseObject();
                if (c == '[') return ParseArray();
                if (c == '"') return ParseString();
                if (c == '-' || char.IsDigit(c)) return ParseNumber();
                if (Match("true")) return true;
                if (Match("false")) return false;
                if (Match("null")) return null;

                throw new FormatException($"Unexpected JSON token '{c}' at index {_index}.");
            }

            public void SkipWhitespace()
            {
                while (!IsEnd && char.IsWhiteSpace(_json[_index]))
                    _index++;
            }

            Dictionary<string, object> ParseObject()
            {
                var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                Expect('{');
                SkipWhitespace();
                if (TryConsume('}'))
                    return dict;

                while (true)
                {
                    SkipWhitespace();
                    string key = ParseString();
                    SkipWhitespace();
                    Expect(':');
                    dict[key] = ParseValue();
                    SkipWhitespace();
                    if (TryConsume('}'))
                        return dict;
                    Expect(',');
                }
            }

            List<object> ParseArray()
            {
                var list = new List<object>();
                Expect('[');
                SkipWhitespace();
                if (TryConsume(']'))
                    return list;

                while (true)
                {
                    list.Add(ParseValue());
                    SkipWhitespace();
                    if (TryConsume(']'))
                        return list;
                    Expect(',');
                }
            }

            string ParseString()
            {
                Expect('"');
                var chars = new System.Text.StringBuilder();
                while (!IsEnd)
                {
                    char c = _json[_index++];
                    if (c == '"')
                        return chars.ToString();

                    if (c != '\\')
                    {
                        chars.Append(c);
                        continue;
                    }

                    if (IsEnd)
                        throw new FormatException("Invalid JSON string escape.");

                    char escape = _json[_index++];
                    switch (escape)
                    {
                        case '"': chars.Append('"'); break;
                        case '\\': chars.Append('\\'); break;
                        case '/': chars.Append('/'); break;
                        case 'b': chars.Append('\b'); break;
                        case 'f': chars.Append('\f'); break;
                        case 'n': chars.Append('\n'); break;
                        case 'r': chars.Append('\r'); break;
                        case 't': chars.Append('\t'); break;
                        case 'u':
                            chars.Append(ParseUnicodeEscape());
                            break;
                        default:
                            throw new FormatException($"Invalid JSON string escape '\\{escape}'.");
                    }
                }

                throw new FormatException("Unterminated JSON string.");
            }

            char ParseUnicodeEscape()
            {
                if (_index + 4 > _json.Length)
                    throw new FormatException("Invalid JSON unicode escape.");

                string hex = _json.Substring(_index, 4);
                _index += 4;
                return (char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }

            object ParseNumber()
            {
                int start = _index;
                if (!IsEnd && _json[_index] == '-')
                    _index++;

                while (!IsEnd && char.IsDigit(_json[_index]))
                    _index++;

                if (!IsEnd && _json[_index] == '.')
                {
                    _index++;
                    while (!IsEnd && char.IsDigit(_json[_index]))
                        _index++;
                }

                if (!IsEnd && (_json[_index] == 'e' || _json[_index] == 'E'))
                {
                    _index++;
                    if (!IsEnd && (_json[_index] == '+' || _json[_index] == '-'))
                        _index++;
                    while (!IsEnd && char.IsDigit(_json[_index]))
                        _index++;
                }

                string text = _json.Substring(start, _index - start);
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                    throw new FormatException($"Invalid JSON number '{text}'.");

                return value;
            }

            bool Match(string literal)
            {
                if (_index + literal.Length > _json.Length)
                    return false;

                for (int i = 0; i < literal.Length; i++)
                {
                    if (_json[_index + i] != literal[i])
                        return false;
                }

                _index += literal.Length;
                return true;
            }

            void Expect(char expected)
            {
                SkipWhitespace();
                if (IsEnd || _json[_index] != expected)
                    throw new FormatException($"Expected '{expected}' at JSON index {_index}.");
                _index++;
            }

            bool TryConsume(char expected)
            {
                SkipWhitespace();
                if (!IsEnd && _json[_index] == expected)
                {
                    _index++;
                    return true;
                }
                return false;
            }
        }
    }
}
