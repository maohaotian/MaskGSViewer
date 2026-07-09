// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

[DefaultExecutionOrder(1000)]
[DisallowMultipleComponent]
public sealed class HeadGazeDwellLogger : MonoBehaviour
{
    public enum GazeRegion
    {
        Unknown = 0,
        Clear = 1,
        Transition = 2,
        Peripheral = 3
    }

    [Serializable]
    struct GazeSample
    {
        public float elapsedSeconds;
        public float totalSeconds;
        public StudyContext context;
        public GazeRegion region;
        public float focusT;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 euler;
        public Quaternion relativeRotation;
        public Vector3 relativeEuler;
        public float horizontalAngle;
        public float verticalAngle;
        public float angularDistance;
    }

    struct StudyContext
    {
        public bool studyRunning;
        public string participantId;
        public string sequenceLabel;
        public int sequencePosition;
        public int sequenceCount;
        public int stimulusIndex;
        public string stimulusLabel;
        public string splatFile;
        public string splatPath;
        public int modeNumber;
        public string modeName;
    }

    sealed class DwellAggregate
    {
        public StudyContext context;
        public float totalTime;
        public float clearTime;
        public float transitionTime;
        public float peripheralTime;
        public float unknownTime;
    }

    [Header("References")]
    [Tooltip("Camera to sample. Auto-resolves to the attached camera or Camera.main.")]
    public Camera targetCamera;

    [Tooltip("WorldFocusBlurController used for region evaluation. Auto-resolves if empty.")]
    public WorldFocusBlurController focusBlur;

    [Tooltip("Optional file browser reference used to pause logging while the browser is open.")]
    public VRFileBrowser fileBrowser;

    [Tooltip("Optional user study flow context. Auto-resolves if empty.")]
    public UserStudyFlowController studyFlow;

    [Header("Recording")]
    [Tooltip("Start logging automatically on enable.")]
    public bool autoStart = true;

    [Tooltip("Reset all counters when recording starts.")]
    public bool resetOnStart = true;

    [Tooltip("Pause accumulation while the VR file browser is open.")]
    public bool pauseWhenBrowserOpen = true;

    [Tooltip("When a user study flow is enabled, wait until the participant ID has been submitted and the first asset is active.")]
    public bool pauseWhenStudyNotRunning = true;

    [Tooltip("Pause accumulation while the study flow is showing a loading panel.")]
    public bool pauseWhenStudyLoading = true;

    [Tooltip("Require the blur controller to be enabled and its effect flag to be on before classifying regions.")]
    public bool requireActiveFocusEffect = true;

    [Tooltip("Use unscaled time for dwell accumulation.")]
    public bool useUnscaledTime = true;

    [Tooltip("Record a sample snapshot at this interval while recording.")]
    [Min(0.02f)]
    public float sampleInterval = 0.1f;

    [Tooltip("Keep per-frame samples in memory and write them to disk.")]
    public bool logSamples = true;

    [Tooltip("Write files when recording stops.")]
    public bool saveOnStop = true;

    [Tooltip("Write files on application quit if there is unsaved data.")]
    public bool saveOnApplicationQuit = true;

    [Tooltip("Subfolder under the current working directory.")]
    public string logFolderName = "HeadGazeLogs";

    [Tooltip("Create an id-<participant> subfolder below Log Folder Name.")]
    public bool groupLogsByParticipant = true;

    [Tooltip("Create a session-<timestamp> subfolder below the participant folder.")]
    public bool groupLogsBySession = true;

    [Tooltip("File prefix for generated logs.")]
    public string fileNamePrefix = "head_gaze";

    [Header("Region Thresholds")]
    [Tooltip("FocusT values at or below this threshold count as fully clear.")]
    [Range(0f, 0.1f)]
    public float clearThreshold = 0.001f;

    [Tooltip("FocusT values at or above this threshold count as fully peripheral.")]
    [Range(0.9f, 1f)]
    public float peripheralThreshold = 0.999f;

    [Header("Runtime Status")]
    [SerializeField] bool recording;
    [SerializeField] GazeRegion currentRegion = GazeRegion.Unknown;
    [SerializeField] float currentFocusT = 1f;
    [SerializeField] float totalTime;
    [SerializeField] float clearTime;
    [SerializeField] float transitionTime;
    [SerializeField] float peripheralTime;
    [SerializeField] float unknownTime;
    [SerializeField] string lastSummaryPath;
    [SerializeField] string lastSamplesPath;

    readonly List<GazeSample> _samples = new List<GazeSample>();
    readonly Dictionary<string, DwellAggregate> _contextTotals = new Dictionary<string, DwellAggregate>();
    Quaternion _referenceRotation = Quaternion.identity;
    float _nextSampleTime;
    float _sessionStartClock;
    bool _hasReferenceRotation;
    bool _hasUnsavedData;

    [ContextMenu("Start Recording")]
    public void StartRecording()
    {
        ResolveReferences();

        if (resetOnStart)
            ResetStats();

        _referenceRotation = targetCamera != null ? targetCamera.transform.rotation : transform.rotation;
        _sessionStartClock = CurrentClock();
        _hasReferenceRotation = false;
        _nextSampleTime = 0f;
        recording = true;
        Debug.Log("[HeadGazeDwellLogger] Recording started.");
    }

    [ContextMenu("Stop Recording")]
    public void StopRecording()
    {
        if (!recording)
            return;

        recording = false;
        if (saveOnStop)
            FlushLog();
    }

    [ContextMenu("Reset Stats")]
    public void ResetStats()
    {
        totalTime = 0f;
        clearTime = 0f;
        transitionTime = 0f;
        peripheralTime = 0f;
        unknownTime = 0f;
        currentRegion = GazeRegion.Unknown;
        currentFocusT = 1f;
        _samples.Clear();
        _contextTotals.Clear();
        _nextSampleTime = 0f;
        _hasReferenceRotation = false;
        _hasUnsavedData = false;
        lastSummaryPath = string.Empty;
        lastSamplesPath = string.Empty;
    }

    [ContextMenu("Flush Log Now")]
    public void FlushLog()
    {
        if (!_hasUnsavedData)
            return;

        ResolveReferences();

        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string baseName = MakeSafeFilePart(fileNamePrefix);
        List<string> summaryPaths = new List<string>();
        List<string> samplePaths = new List<string>();
        HashSet<string> logFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_contextTotals.Count == 0)
        {
            StudyContext context = CaptureStudyContext();
            string folder = ResolveLogFolder(context, stamp);
            Directory.CreateDirectory(folder);
            logFolders.Add(folder);

            string fileStem = MakeLogFileStem(baseName, context, stamp);
            string summaryPath = Path.Combine(folder, fileStem + "_summary.csv");
            string samplesPath = Path.Combine(folder, fileStem + "_samples.csv");

            WriteSummaryCsv(summaryPath);
            summaryPaths.Add(summaryPath);

            if (logSamples)
            {
                WriteSamplesCsv(samplesPath);
                samplePaths.Add(samplesPath);
            }
        }
        else
        {
            foreach (var aggregate in _contextTotals.Values)
            {
                string contextKey = MakeContextKey(aggregate.context);
                string folder = ResolveLogFolder(aggregate.context, stamp);
                Directory.CreateDirectory(folder);
                logFolders.Add(folder);

                string fileStem = MakeLogFileStem(baseName, aggregate.context, stamp);
                string summaryPath = Path.Combine(folder, fileStem + "_summary.csv");
                string samplesPath = Path.Combine(folder, fileStem + "_samples.csv");

                WriteSummaryCsv(summaryPath, aggregate);
                summaryPaths.Add(summaryPath);

                if (logSamples)
                {
                    WriteSamplesCsv(samplesPath, contextKey);
                    samplePaths.Add(samplesPath);
                }
            }
        }

        lastSummaryPath = string.Join("; ", summaryPaths);
        lastSamplesPath = string.Join("; ", samplePaths);
        _hasUnsavedData = false;

        Debug.Log($"[HeadGazeDwellLogger] Saved {summaryPaths.Count} summary log(s) to {string.Join("; ", logFolders)}.");
        if (logSamples)
            Debug.Log($"[HeadGazeDwellLogger] Saved {samplePaths.Count} sample log(s) to {string.Join("; ", logFolders)}.");
    }

    void OnEnable()
    {
        ResolveReferences();
        if (autoStart)
            StartRecording();
    }

    void OnDisable()
    {
        if (recording && saveOnStop)
            StopRecording();
    }

    void OnApplicationQuit()
    {
        if (!saveOnApplicationQuit)
            return;

        if (recording)
            recording = false;

        if (_hasUnsavedData)
            FlushLog();
    }

    void Update()
    {
        if (!recording)
            return;

        ResolveReferences();

        if (pauseWhenBrowserOpen && fileBrowser != null && fileBrowser.IsOpen)
            return;

        if (pauseWhenStudyNotRunning
            && studyFlow != null
            && studyFlow.studyModeEnabled
            && !studyFlow.IsStudyRunning)
            return;

        if (pauseWhenStudyLoading && studyFlow != null && studyFlow.IsLoading)
            return;

        float deltaTime = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;
        if (deltaTime <= 0f)
            return;

        if (!_hasReferenceRotation)
        {
            Transform cameraTransform = targetCamera != null ? targetCamera.transform : transform;
            _referenceRotation = cameraTransform.rotation;
            _sessionStartClock = CurrentClock();
            _nextSampleTime = 0f;
            _hasReferenceRotation = true;
        }

        totalTime += deltaTime;

        GazeRegion region = GazeRegion.Unknown;
        float focusT = 1f;
        WorldFocusBlurController.FocusEvaluation evaluation = default(WorldFocusBlurController.FocusEvaluation);
        bool canClassify = focusBlur != null
            && (!requireActiveFocusEffect || (focusBlur.isActiveAndEnabled && focusBlur.effectEnabled))
            && targetCamera != null
            && focusBlur.TryEvaluateFocus(targetCamera, out evaluation);

        if (canClassify)
        {
            focusT = evaluation.FocusT;
            if (focusT <= clearThreshold)
                region = GazeRegion.Clear;
            else if (focusT >= peripheralThreshold)
                region = GazeRegion.Peripheral;
            else
                region = GazeRegion.Transition;
        }

        currentRegion = region;
        currentFocusT = focusT;

        StudyContext context = CaptureStudyContext();
        switch (region)
        {
            case GazeRegion.Clear:
                clearTime += deltaTime;
                break;
            case GazeRegion.Transition:
                transitionTime += deltaTime;
                break;
            case GazeRegion.Peripheral:
                peripheralTime += deltaTime;
                break;
            default:
                unknownTime += deltaTime;
                break;
        }
        AccumulateContext(context, region, deltaTime);

        if (logSamples && sampleInterval > 0f && totalTime + 1e-5f >= _nextSampleTime)
        {
            while (totalTime + 1e-5f >= _nextSampleTime)
            {
                _samples.Add(CreateSample(region, focusT, evaluation, context));
                _nextSampleTime += sampleInterval;
            }
        }

        _hasUnsavedData = true;
    }

    GazeSample CreateSample(GazeRegion region, float focusT, WorldFocusBlurController.FocusEvaluation evaluation, StudyContext context)
    {
        Transform cameraTransform = targetCamera != null ? targetCamera.transform : transform;
        Quaternion rotation = cameraTransform.rotation;
        Quaternion relativeRotation = Quaternion.Inverse(_referenceRotation) * rotation;

        return new GazeSample
        {
            elapsedSeconds = Mathf.Max(0f, CurrentClock() - _sessionStartClock),
            totalSeconds = totalTime,
            context = context,
            region = region,
            focusT = focusT,
            position = cameraTransform.position,
            rotation = rotation,
            euler = NormalizeEuler(rotation.eulerAngles),
            relativeRotation = relativeRotation,
            relativeEuler = NormalizeEuler(relativeRotation.eulerAngles),
            horizontalAngle = evaluation.HorizontalAngle,
            verticalAngle = evaluation.VerticalAngle,
            angularDistance = evaluation.AngularDistance
        };
    }

    void WriteSummaryCsv(string path)
    {
        using (var writer = new StreamWriter(path, false, Encoding.UTF8))
        {
            WriteSummaryHeader(writer);

            if (_contextTotals.Count == 0)
            {
                WriteSummaryRow(writer, CaptureStudyContext(), totalTime, clearTime, transitionTime, peripheralTime, unknownTime);
                return;
            }

            foreach (var aggregate in _contextTotals.Values)
            {
                WriteSummaryRow(writer,
                    aggregate.context,
                    aggregate.totalTime,
                    aggregate.clearTime,
                    aggregate.transitionTime,
                    aggregate.peripheralTime,
                    aggregate.unknownTime);
            }
        }
    }

    void WriteSummaryCsv(string path, DwellAggregate aggregate)
    {
        using (var writer = new StreamWriter(path, false, Encoding.UTF8))
        {
            WriteSummaryHeader(writer);
            WriteSummaryRow(writer,
                aggregate.context,
                aggregate.totalTime,
                aggregate.clearTime,
                aggregate.transitionTime,
                aggregate.peripheralTime,
                aggregate.unknownTime);
        }
    }

    void WriteSamplesCsv(string path)
    {
        WriteSamplesCsv(path, null);
    }

    void WriteSamplesCsv(string path, string contextKey)
    {
        using (var writer = new StreamWriter(path, false, Encoding.UTF8))
        {
            WriteCsvRow(writer,
                "study_running",
                "participant_id",
                "asset_position_1based",
                "asset_count",
                "asset_index_0based",
                "asset_label",
                "splat_file",
                "splat_path",
                "mode_number",
                "mode_name",
                "elapsed_seconds",
                "total_seconds",
                "region",
                "focus_t",
                "pos_x",
                "pos_y",
                "pos_z",
                "rot_x",
                "rot_y",
                "rot_z",
                "rot_w",
                "euler_x",
                "euler_y",
                "euler_z",
                "relative_rot_x",
                "relative_rot_y",
                "relative_rot_z",
                "relative_rot_w",
                "relative_euler_x",
                "relative_euler_y",
                "relative_euler_z",
                "horizontal_angle",
                "vertical_angle",
                "angular_distance");

            for (int i = 0; i < _samples.Count; i++)
            {
                GazeSample sample = _samples[i];
                if (contextKey != null && MakeContextKey(sample.context) != contextKey)
                    continue;

                WriteCsvRow(writer,
                    sample.context.studyRunning ? "1" : "0",
                    sample.context.participantId,
                    sample.context.sequencePosition.ToString(CultureInfo.InvariantCulture),
                    sample.context.sequenceCount.ToString(CultureInfo.InvariantCulture),
                    sample.context.stimulusIndex.ToString(CultureInfo.InvariantCulture),
                    sample.context.stimulusLabel,
                    sample.context.splatFile,
                    sample.context.splatPath,
                    sample.context.modeNumber.ToString(CultureInfo.InvariantCulture),
                    sample.context.modeName,
                    FormatFloat(sample.elapsedSeconds),
                    FormatFloat(sample.totalSeconds),
                    sample.region.ToString(),
                    FormatFloat(sample.focusT),
                    FormatFloat(sample.position.x),
                    FormatFloat(sample.position.y),
                    FormatFloat(sample.position.z),
                    FormatFloat(sample.rotation.x),
                    FormatFloat(sample.rotation.y),
                    FormatFloat(sample.rotation.z),
                    FormatFloat(sample.rotation.w),
                    FormatFloat(sample.euler.x),
                    FormatFloat(sample.euler.y),
                    FormatFloat(sample.euler.z),
                    FormatFloat(sample.relativeRotation.x),
                    FormatFloat(sample.relativeRotation.y),
                    FormatFloat(sample.relativeRotation.z),
                    FormatFloat(sample.relativeRotation.w),
                    FormatFloat(sample.relativeEuler.x),
                    FormatFloat(sample.relativeEuler.y),
                    FormatFloat(sample.relativeEuler.z),
                    FormatFloat(sample.horizontalAngle),
                    FormatFloat(sample.verticalAngle),
                    FormatFloat(sample.angularDistance));
            }
        }
    }

    static void WriteSummaryHeader(TextWriter writer)
    {
        WriteCsvRow(writer,
            "study_running",
            "participant_id",
            "asset_position_1based",
            "asset_count",
            "asset_index_0based",
            "asset_label",
            "splat_file",
            "splat_path",
            "mode_number",
            "mode_name",
            "total_time_seconds",
            "clear_time_seconds",
            "transition_time_seconds",
            "peripheral_time_seconds",
            "unknown_time_seconds",
            "clear_ratio",
            "transition_ratio",
            "peripheral_ratio",
            "unknown_ratio");
    }

    static void WriteCsvRow(TextWriter writer, params string[] cells)
    {
        for (int i = 0; i < cells.Length; i++)
        {
            if (i > 0)
                writer.Write(',');
            writer.Write(EscapeCsvCell(cells[i]));
        }

        writer.WriteLine();
    }

    void WriteSummaryRow(
        TextWriter writer,
        StudyContext context,
        float aggregateTotalTime,
        float aggregateClearTime,
        float aggregateTransitionTime,
        float aggregatePeripheralTime,
        float aggregateUnknownTime)
    {
        WriteCsvRow(writer,
            context.studyRunning ? "1" : "0",
            context.participantId,
            context.sequencePosition.ToString(CultureInfo.InvariantCulture),
            context.sequenceCount.ToString(CultureInfo.InvariantCulture),
            context.stimulusIndex.ToString(CultureInfo.InvariantCulture),
            context.stimulusLabel,
            context.splatFile,
            context.splatPath,
            context.modeNumber.ToString(CultureInfo.InvariantCulture),
            context.modeName,
            FormatFloat(aggregateTotalTime),
            FormatFloat(aggregateClearTime),
            FormatFloat(aggregateTransitionTime),
            FormatFloat(aggregatePeripheralTime),
            FormatFloat(aggregateUnknownTime),
            FormatFloat(SafeRatio(aggregateClearTime, aggregateTotalTime)),
            FormatFloat(SafeRatio(aggregateTransitionTime, aggregateTotalTime)),
            FormatFloat(SafeRatio(aggregatePeripheralTime, aggregateTotalTime)),
            FormatFloat(SafeRatio(aggregateUnknownTime, aggregateTotalTime)));
    }

    void AccumulateContext(StudyContext context, GazeRegion region, float deltaTime)
    {
        string key = MakeContextKey(context);
        if (!_contextTotals.TryGetValue(key, out DwellAggregate aggregate))
        {
            aggregate = new DwellAggregate { context = context };
            _contextTotals.Add(key, aggregate);
        }

        aggregate.totalTime += deltaTime;
        switch (region)
        {
            case GazeRegion.Clear:
                aggregate.clearTime += deltaTime;
                break;
            case GazeRegion.Transition:
                aggregate.transitionTime += deltaTime;
                break;
            case GazeRegion.Peripheral:
                aggregate.peripheralTime += deltaTime;
                break;
            default:
                aggregate.unknownTime += deltaTime;
                break;
        }
    }

    StudyContext CaptureStudyContext()
    {
        if (studyFlow == null)
        {
            return new StudyContext
            {
                studyRunning = false,
                participantId = "",
                sequenceLabel = "",
                sequencePosition = 0,
                sequenceCount = 0,
                stimulusIndex = -1,
                stimulusLabel = "",
                splatFile = "",
                splatPath = "",
                modeNumber = 0,
                modeName = "Debug"
            };
        }

        string splatPath = studyFlow.CurrentSplatPath ?? "";
        return new StudyContext
        {
            studyRunning = studyFlow.IsStudyRunning,
            participantId = studyFlow.ParticipantId ?? "",
            sequenceLabel = studyFlow.ActiveSequenceLabel ?? "",
            sequencePosition = studyFlow.CurrentSequencePositionOneBased,
            sequenceCount = studyFlow.CurrentSequenceCount,
            stimulusIndex = studyFlow.CurrentStimulusIndex,
            stimulusLabel = studyFlow.ActiveStimulusLabel ?? "",
            splatFile = string.IsNullOrWhiteSpace(splatPath) ? "" : Path.GetFileName(splatPath),
            splatPath = splatPath,
            modeNumber = studyFlow.CurrentModeNumber,
            modeName = studyFlow.CurrentModeName ?? ""
        };
    }

    static string MakeContextKey(StudyContext context)
    {
        const char separator = '\u001f';
        return string.Join(separator.ToString(), new[]
        {
            context.studyRunning ? "1" : "0",
            context.participantId ?? "",
            context.sequenceLabel ?? "",
            context.sequencePosition.ToString(CultureInfo.InvariantCulture),
            context.sequenceCount.ToString(CultureInfo.InvariantCulture),
            context.stimulusIndex.ToString(CultureInfo.InvariantCulture),
            context.stimulusLabel ?? "",
            context.splatPath ?? "",
            context.modeNumber.ToString(CultureInfo.InvariantCulture),
            context.modeName ?? ""
        });
    }

    static string MakeLogFileStem(string baseName, StudyContext context, string stamp)
    {
        return baseName + "_" + MakeContextFilePart(context) + "_" + stamp;
    }

    static string MakeContextFilePart(StudyContext context)
    {
        string participant = string.IsNullOrWhiteSpace(context.participantId)
            ? "id-unknown"
            : "id-" + context.participantId.Trim();
        string mode = context.modeNumber > 0
            ? "mode-" + context.modeNumber.ToString(CultureInfo.InvariantCulture) + "-" + SafeLabel(context.modeName, "unknown")
            : "mode-debug";
        string sequence = context.sequencePosition > 0
            ? "seq-" + context.sequencePosition.ToString("000", CultureInfo.InvariantCulture)
            : "seq-000";
        string scene = "scene-" + ResolveSceneLabel(context);

        return MakeSafeFilePart(participant)
            + "_" + MakeSafeFilePart(mode)
            + "_" + MakeSafeFilePart(sequence)
            + "_" + MakeSafeFilePart(scene);
    }

    static string ResolveSceneLabel(StudyContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.stimulusLabel))
            return context.stimulusLabel.Trim();

        if (!string.IsNullOrWhiteSpace(context.splatFile))
            return Path.GetFileNameWithoutExtension(context.splatFile);

        return "unknown";
    }

    static string SafeLabel(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    static string EscapeCsvCell(string value)
    {
        if (value == null)
            return string.Empty;

        bool needsQuotes = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
        if (!needsQuotes)
            return value;

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    static string FormatFloat(float value)
    {
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    static float SafeRatio(float numerator, float denominator)
    {
        return denominator > 1e-6f ? numerator / denominator : 0f;
    }

    static Vector3 NormalizeEuler(Vector3 euler)
    {
        return new Vector3(
            NormalizeAngle(euler.x),
            NormalizeAngle(euler.y),
            NormalizeAngle(euler.z));
    }

    static float NormalizeAngle(float angle)
    {
        return Mathf.DeltaAngle(0f, angle);
    }

    static string MakeSafeFilePart(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "head_gaze";

        char[] invalid = Path.GetInvalidFileNameChars();
        StringBuilder builder = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            builder.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }

        return builder.Length > 0 ? builder.ToString() : "head_gaze";
    }

    string ResolveLogFolder(StudyContext context, string stamp)
    {
        string folder = ResolveBaseLogFolder();

        if (groupLogsByParticipant)
            folder = Path.Combine(folder, MakeSafeFilePart("id-" + SafeLabel(context.participantId, "unknown")));

        if (groupLogsBySession)
            folder = Path.Combine(folder, MakeSafeFilePart("session-" + SafeLabel(stamp, "unknown")));

        return folder;
    }

    string ResolveBaseLogFolder()
    {
        string baseDirectory = Directory.GetCurrentDirectory();
        if (string.IsNullOrWhiteSpace(logFolderName))
            return baseDirectory;

        if (Path.IsPathRooted(logFolderName))
            return logFolderName;

        return Path.GetFullPath(Path.Combine(baseDirectory, logFolderName));
    }

    float CurrentClock()
    {
        return useUnscaledTime ? Time.unscaledTime : Time.time;
    }

    void ResolveReferences()
    {
        if (targetCamera == null)
            targetCamera = GetComponent<Camera>();

        if (targetCamera == null)
            targetCamera = Camera.main;

        if (focusBlur == null)
            focusBlur = GetComponent<WorldFocusBlurController>();

        if (focusBlur == null)
            focusBlur = FindAnyObjectByType<WorldFocusBlurController>();

        if (fileBrowser == null)
            fileBrowser = FindAnyObjectByType<VRFileBrowser>();

        if (studyFlow == null)
            studyFlow = FindAnyObjectByType<UserStudyFlowController>();
    }
}
