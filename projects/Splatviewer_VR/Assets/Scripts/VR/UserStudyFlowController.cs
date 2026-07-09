// SPDX-License-Identifier: MIT
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.UI;

[DefaultExecutionOrder(-700)]
[DisallowMultipleComponent]
public sealed class UserStudyFlowController : MonoBehaviour
{
    public enum StudyMode
    {
        PhotoScreen = 1,
        MaskedContent = 2,
        FrontHemisphereMask = 3,
        Unmasked360 = 4
    }

    public enum SequenceSelectionMode
    {
        AsConfigured = 0,
        RotateByParticipantId = 1,
        ShuffleByParticipantId = 2,
        ExplicitSequences = 3
    }

    [Serializable]
    public sealed class StudyStimulus
    {
        public string label;
        public UnityEngine.Object splatAsset;
        public string splatPath;
        public string photoPath;
    }

    [Serializable]
    public sealed class StudySequence
    {
        public string label;
        public int[] stimulusIndices;
    }

    [Header("Enable")]
    [Tooltip("When enabled, this controller owns study input and disables the normal viewer controls below.")]
    public bool studyModeEnabled = true;

    [Tooltip("Ask for participant ID before the first asset is shown.")]
    public bool promptForParticipantId = true;

    [Tooltip("Optional ID used when Prompt For Participant Id is disabled.")]
    public string participantId = "";

    [Tooltip("Mode applied after the participant ID is accepted.")]
    public StudyMode defaultMode = StudyMode.MaskedContent;

    [Header("References")]
    public RuntimeSplatLoader loader;
    public GaussianSplatRenderer splatRenderer;
    public VRFileBrowser fileBrowser;
    public SplatCycler splatCycler;
    public CameraTrajectoryPlayer trajectoryPlayer;
    public VRLocomotion locomotion;
    public SplatRotator splatRotator;
    public WorldFocusBlurController focusBlur;
    public VRRig rig;
    public Camera targetCamera;

    [Header("GS Asset Sequence")]
    [Tooltip("Folder used for relative GS/photo paths and optional auto scan.")]
    [InspectorName("Asset Folder")]
    public string stimulusFolder = "GaussianAssets";

    [Tooltip("Auto-scan Asset Folder if the detailed asset list is empty at runtime.")]
    [InspectorName("Auto Scan Asset Folder When Empty")]
    public bool autoScanStimulusFolderWhenEmpty = true;

    [Tooltip("Simple ordered GS asset list. Drag GaussianSplatAsset entries or .ply/.spz/.sog project files here; this list is used first.")]
    [InspectorName("GS Asset Sequence (Drag Here)")]
    public List<UnityEngine.Object> orderedSplatAssets = new List<UnityEngine.Object>();

    [Tooltip("Detailed GS asset list. Use this when you need custom labels or file paths; photos are matched by GS file name in the same folder.")]
    [InspectorName("Detailed Asset List")]
    public List<StudyStimulus> stimuli = new List<StudyStimulus>();

    [HideInInspector]
    public SequenceSelectionMode sequenceSelection = SequenceSelectionMode.AsConfigured;

    [HideInInspector]
    public List<StudySequence> explicitSequences = new List<StudySequence>();

    [Header("Mode 2 Mask")]
    [Range(1f, 89f)]
    public float mode2InnerAngle = 20f;

    [Range(1f, 89f)]
    public float mode2OuterAngle = 42f;

    [Header("Mode 3 Mask")]
    [Tooltip("Recapture the forward direction whenever entering a mask mode or changing asset.")]
    public bool recaptureMaskForwardOnApply = true;

    [Header("Photo Screen")]
    public float photoScreenDistance = 2f;
    public float photoScreenWidthMeters = 2.4f;
    public bool photoScreenFollowsHead = false;

    [Header("Loading")]
    public bool showLoadingPanel = true;
    public float loadingPanelDistance = 1.5f;
    public string loadingTitle = "Loading";

    [Header("Study Ownership")]
    public bool closeAndDisableFileBrowser = true;
    public bool disableSplatCycler = true;
    public bool disableLocomotion = true;
    public bool disableTrajectoryPlayerInput = true;
    public bool disableSplatRotator = true;
    public bool restoreDisabledBehavioursOnDisable = true;
    public Behaviour[] additionalBehavioursToDisable;

    [Header("Trajectory")]
    public bool loadMatchingTrajectoryForSplat = true;
    public bool resetRigWhenNoTrajectory = true;

    [Header("Runtime Status")]
    [SerializeField] bool _studyRunning;
    [SerializeField] int _currentSequencePosition = -1;
    [SerializeField] int _currentStimulusIndex = -1;
    [SerializeField] StudyMode _currentMode = StudyMode.MaskedContent;
    [SerializeField] string _activeSequenceLabel = "";
    [SerializeField] string _activeStimulusLabel = "";

    readonly List<int> _runtimeSequence = new List<int>();
    readonly Dictionary<Behaviour, bool> _previousBehaviourStates = new Dictionary<Behaviour, bool>();

    GameObject _participantPanelRoot;
    Text _participantTitleText;
    Text _participantValueText;
    Text _participantHintText;

    GameObject _loadingPanelRoot;
    Text _loadingTitleText;
    Text _loadingDetailText;

    GameObject _photoScreenRoot;
    RectTransform _photoCanvasRect;
    RawImage _photoImage;
    Text _photoMissingText;
    Texture2D _loadedPhotoTexture;
    string _loadedPhotoPath;
    string _loadedSplatPath;
    string _activeSplatPath;
    string _activePhotoPath;
    bool _ownershipApplied;
    bool _participantPromptVisible;
    bool _loadingInProgress;

    readonly List<StudyStimulus> _studyStimuli = new List<StudyStimulus>();
    static Font s_Font;

    public bool IsStudyRunning => _studyRunning;
    public string ParticipantId => participantId;
    public int CurrentModeNumber => (int)_currentMode;
    public string CurrentModeName => _currentMode.ToString();
    public int CurrentSequencePosition => _currentSequencePosition;
    public int CurrentSequencePositionOneBased => _currentSequencePosition >= 0 ? _currentSequencePosition + 1 : 0;
    public int CurrentSequenceCount => _runtimeSequence.Count;
    public int CurrentStimulusIndex => _currentStimulusIndex;
    public string ActiveSequenceLabel => _activeSequenceLabel;
    public string ActiveStimulusLabel => _activeStimulusLabel;
    public string CurrentSplatPath => _activeSplatPath;
    public string CurrentPhotoPath => _activePhotoPath;
    public bool IsLoading => _loadingInProgress;

    void Awake()
    {
        ResolveReferences();
        BuildParticipantPanel();
        BuildLoadingPanel();
        BuildPhotoScreen();
    }

    void OnEnable()
    {
        if (!studyModeEnabled)
            return;

        ResolveReferences();
        ApplyStudyOwnership();
    }

    void Start()
    {
        if (!studyModeEnabled)
        {
            HideParticipantPrompt();
            ShowPhotoScreen(false);
            return;
        }

        ResolveReferences();
        ApplyStudyOwnership();

        if (promptForParticipantId)
            ShowParticipantPrompt();
        else
            BeginStudy();
    }

    void OnDisable()
    {
        if (restoreDisabledBehavioursOnDisable)
            RestoreStudyOwnership();

        _loadingInProgress = false;
        ShowPhotoScreen(false);
        HideLoadingPanel();
        HideParticipantPrompt();
    }

    void OnDestroy()
    {
        if (_loadedPhotoTexture != null)
            Destroy(_loadedPhotoTexture);
    }

    void Update()
    {
        if (!studyModeEnabled)
            return;

        if (_loadingInProgress)
            return;

        if (_participantPromptVisible)
        {
            HandleParticipantInput();
            return;
        }

        if (!_studyRunning)
            return;

        HandleStudyInput();
    }

    void LateUpdate()
    {
        if (_participantPromptVisible)
            PlaceParticipantPanelInFront();

        if (_loadingPanelRoot != null && _loadingPanelRoot.activeSelf)
            PlaceLoadingPanelInFront();

        if (_photoScreenRoot != null && _photoScreenRoot.activeSelf && photoScreenFollowsHead)
            PlacePhotoScreenInFront();
    }

    [ContextMenu("Begin Study")]
    public void BeginStudy()
    {
        if (_loadingInProgress)
            return;

        _loadingInProgress = true;
        StartCoroutine(BeginStudyRoutine());
    }

    IEnumerator BeginStudyRoutine()
    {
        ResolveReferences();
        ApplyStudyOwnership();
        HideParticipantPrompt();

        if (string.IsNullOrWhiteSpace(participantId))
            participantId = "anonymous";

        BuildStudyStimuli();
        BuildRuntimeSequence();

        if (_runtimeSequence.Count == 0)
        {
            Debug.LogWarning("[UserStudyFlowController] No study assets are configured.");
            HideLoadingPanel();
            _loadingInProgress = false;
            yield break;
        }

        _studyRunning = true;
        _currentMode = defaultMode;
        _currentSequencePosition = 0;
        yield return ApplyCurrentStimulusWithLoading(true, loadingTitle);

        Debug.Log($"[UserStudyFlowController] Study started. participant={participantId}, sequence={_activeSequenceLabel}, count={_runtimeSequence.Count}");
    }

    [ContextMenu("Next Stimulus")]
    public void NextStimulus()
    {
        if (!_studyRunning || _runtimeSequence.Count == 0 || _loadingInProgress)
            return;

        int nextPosition = (_currentSequencePosition + 1) % _runtimeSequence.Count;
        _loadingInProgress = true;
        StartCoroutine(SetSequencePositionRoutine(nextPosition, true));
    }

    [ContextMenu("Previous Stimulus")]
    public void PreviousStimulus()
    {
        if (!_studyRunning || _runtimeSequence.Count == 0 || _loadingInProgress)
            return;

        int previousPosition = (_currentSequencePosition - 1 + _runtimeSequence.Count) % _runtimeSequence.Count;
        _loadingInProgress = true;
        StartCoroutine(SetSequencePositionRoutine(previousPosition, true));
    }

    public void SetMode(int modeNumber)
    {
        if (modeNumber < 1 || modeNumber > 4)
            return;

        SetMode((StudyMode)modeNumber);
    }

    public void SetMode(StudyMode mode)
    {
        if (!_studyRunning || _loadingInProgress)
            return;

        _loadingInProgress = true;
        StartCoroutine(SetModeRoutine(mode));
    }

    IEnumerator SetSequencePositionRoutine(int sequencePosition, bool stimulusChanged)
    {
        _currentSequencePosition = sequencePosition;
        yield return ApplyCurrentStimulusWithLoading(stimulusChanged, loadingTitle);
    }

    IEnumerator SetModeRoutine(StudyMode mode)
    {
        _currentMode = mode;
        yield return ApplyCurrentStimulusWithLoading(false, loadingTitle);
    }

    IEnumerator ApplyCurrentStimulusWithLoading(bool stimulusChanged, string title)
    {
        _loadingInProgress = true;
        try
        {
            ShowLoadingPanel(title, ResolveCurrentLoadingDetail());
            yield return null;

            ApplyCurrentStimulus(stimulusChanged);

            yield return null;
        }
        finally
        {
            HideLoadingPanel();
            _loadingInProgress = false;
        }
    }

    void HandleStudyInput()
    {
        if (Input.GetKeyDown(KeyCode.RightArrow))
            NextStimulus();
        if (Input.GetKeyDown(KeyCode.LeftArrow))
            PreviousStimulus();

        if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1))
            SetMode(StudyMode.PhotoScreen);
        if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2))
            SetMode(StudyMode.MaskedContent);
        if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3))
            SetMode(StudyMode.FrontHemisphereMask);
        if (Input.GetKeyDown(KeyCode.Alpha4) || Input.GetKeyDown(KeyCode.Keypad4))
            SetMode(StudyMode.Unmasked360);
    }

    void HandleParticipantInput()
    {
        bool submitted = false;
        string typed = Input.inputString;
        for (int i = 0; i < typed.Length; i++)
        {
            char c = typed[i];
            if (c == '\b')
            {
                if (!string.IsNullOrEmpty(participantId))
                    participantId = participantId.Substring(0, participantId.Length - 1);
            }
            else if (c == '\n' || c == '\r')
            {
                submitted = true;
            }
            else if (!char.IsControl(c))
            {
                participantId += c;
            }
        }

        if (submitted || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            BeginStudy();

        UpdateParticipantPromptText();
    }

    void ApplyCurrentStimulus(bool stimulusChanged)
    {
        if (_currentSequencePosition < 0 || _currentSequencePosition >= _runtimeSequence.Count)
            return;

        _currentStimulusIndex = _runtimeSequence[_currentSequencePosition];
        if (_currentStimulusIndex < 0 || _currentStimulusIndex >= _studyStimuli.Count)
            return;

        StudyStimulus stimulus = _studyStimuli[_currentStimulusIndex];
        _activeStimulusLabel = ResolveStimulusLabel(stimulus, _currentStimulusIndex);
        _activeSplatPath = ResolveSplatPath(stimulus);
        _activePhotoPath = ResolvePhotoPath(stimulus);

        bool showSplat = _currentMode != StudyMode.PhotoScreen;
        SetSplatVisible(showSplat);
        ShowPhotoScreen(!showSplat);

        if (showSplat)
        {
            LoadSplat(stimulus, stimulusChanged);
            ApplyMaskMode(_currentMode);
        }
        else
        {
            if (focusBlur != null)
                focusBlur.effectEnabled = false;
            LoadPhoto(stimulus);
            PlacePhotoScreenInFront();
        }

        Debug.Log($"[UserStudyFlowController] Stimulus {_currentSequencePosition + 1}/{_runtimeSequence.Count}: {_activeStimulusLabel}, mode={(int)_currentMode}");
    }

    void LoadSplat(StudyStimulus stimulus, bool stimulusChanged)
    {
        GaussianSplatAsset directAsset = stimulus.splatAsset as GaussianSplatAsset;
        if (directAsset != null)
        {
            if (splatRenderer == null)
                ResolveReferences();

            if (splatRenderer == null)
            {
                Debug.LogWarning($"[UserStudyFlowController] Missing splat renderer for asset {_activeStimulusLabel}.");
                return;
            }

            bool needsAssetAssign = stimulusChanged || !ReferenceEquals(splatRenderer.m_Asset, directAsset);
            if (!needsAssetAssign)
                return;

            splatRenderer.m_Asset = directAsset;
            _loadedSplatPath = null;

            bool assetTrajectoryLoaded = TryLoadMatchingTrajectory(_activeSplatPath);
            if (!assetTrajectoryLoaded && resetRigWhenNoTrajectory && rig != null)
                rig.ResetToSpawnPoint(splatRenderer);

            return;
        }

        if (loader == null)
            return;

        string splatPath = _activeSplatPath;
        if (string.IsNullOrWhiteSpace(splatPath))
        {
            Debug.LogWarning($"[UserStudyFlowController] Missing splat path for asset {_activeStimulusLabel}.");
            return;
        }

        bool needsLoad = stimulusChanged || !PathsEqual(_loadedSplatPath, splatPath);
        if (!needsLoad)
            return;

        if (!loader.LoadFile(splatPath))
            return;

        _loadedSplatPath = splatPath;

        bool fileTrajectoryLoaded = TryLoadMatchingTrajectory(splatPath);

        if (!fileTrajectoryLoaded && resetRigWhenNoTrajectory && rig != null)
            rig.ResetToSpawnPoint(splatRenderer);
    }

    void ApplyMaskMode(StudyMode mode)
    {
        if (focusBlur == null)
            return;

        focusBlur.frontHemisphereOnly = false;

        switch (mode)
        {
            case StudyMode.MaskedContent:
                focusBlur.effectEnabled = true;
                focusBlur.innerAngle = mode2InnerAngle;
                focusBlur.outerAngle = Mathf.Max(mode2InnerAngle + 0.1f, mode2OuterAngle);
                break;

            case StudyMode.FrontHemisphereMask:
                focusBlur.effectEnabled = true;
                focusBlur.frontHemisphereOnly = true;
                break;

            case StudyMode.Unmasked360:
                focusBlur.effectEnabled = false;
                break;

            default:
                focusBlur.effectEnabled = false;
                break;
        }

        if (focusBlur.effectEnabled && recaptureMaskForwardOnApply)
            focusBlur.CaptureCurrentDirection();
    }

    void LoadPhoto(StudyStimulus stimulus)
    {
        string photoPath = _activePhotoPath;
        if (PathsEqual(_loadedPhotoPath, photoPath) && _loadedPhotoTexture != null)
            return;

        if (_loadedPhotoTexture != null)
        {
            Destroy(_loadedPhotoTexture);
            _loadedPhotoTexture = null;
            _loadedPhotoPath = null;
        }

        if (string.IsNullOrWhiteSpace(photoPath) || !File.Exists(photoPath))
        {
            if (_photoImage != null)
                _photoImage.texture = null;
            if (_photoMissingText != null)
            {
                _photoMissingText.gameObject.SetActive(true);
                _photoMissingText.text = "Photo not found\n" + _activeStimulusLabel;
            }
            Debug.LogWarning($"[UserStudyFlowController] Photo not found for asset {_activeStimulusLabel}.");
            return;
        }

        byte[] bytes = File.ReadAllBytes(photoPath);
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!texture.LoadImage(bytes))
        {
            Destroy(texture);
            Debug.LogWarning($"[UserStudyFlowController] Failed to load photo: {photoPath}");
            return;
        }

        texture.name = Path.GetFileNameWithoutExtension(photoPath);
        _loadedPhotoTexture = texture;
        _loadedPhotoPath = photoPath;

        if (_photoImage != null)
            _photoImage.texture = texture;
        if (_photoMissingText != null)
            _photoMissingText.gameObject.SetActive(false);

        ResizePhotoScreen(texture.width, texture.height);
    }

    void SetSplatVisible(bool visible)
    {
        if (splatRenderer != null)
            splatRenderer.enabled = visible;
    }

    void ApplyStudyOwnership()
    {
        if (_ownershipApplied)
            return;

        if (closeAndDisableFileBrowser && fileBrowser != null)
        {
            fileBrowser.CloseBrowser();
            DisableOwnedBehaviour(fileBrowser);
        }

        if (disableSplatCycler)
            DisableOwnedBehaviour(splatCycler);
        if (disableLocomotion)
            DisableOwnedBehaviour(locomotion);
        if (disableTrajectoryPlayerInput)
            DisableOwnedBehaviour(trajectoryPlayer);
        if (disableSplatRotator)
            DisableOwnedBehaviour(splatRotator);

        if (additionalBehavioursToDisable != null)
        {
            for (int i = 0; i < additionalBehavioursToDisable.Length; i++)
                DisableOwnedBehaviour(additionalBehavioursToDisable[i]);
        }

        _ownershipApplied = true;
    }

    void DisableOwnedBehaviour(Behaviour behaviour)
    {
        if (behaviour == null || behaviour == this)
            return;

        if (!_previousBehaviourStates.ContainsKey(behaviour))
            _previousBehaviourStates.Add(behaviour, behaviour.enabled);

        behaviour.enabled = false;
    }

    void RestoreStudyOwnership()
    {
        foreach (var kvp in _previousBehaviourStates)
        {
            if (kvp.Key != null)
                kvp.Key.enabled = kvp.Value;
        }

        _previousBehaviourStates.Clear();
        _ownershipApplied = false;
    }

    void ResolveReferences()
    {
        if (loader == null)
            loader = FindAnyObjectByType<RuntimeSplatLoader>();
        if (splatRenderer == null)
            splatRenderer = loader != null && loader.targetRenderer != null
                ? loader.targetRenderer
                : FindAnyObjectByType<GaussianSplatRenderer>();
        if (fileBrowser == null)
            fileBrowser = FindAnyObjectByType<VRFileBrowser>();
        if (splatCycler == null)
            splatCycler = FindAnyObjectByType<SplatCycler>();
        if (trajectoryPlayer == null)
            trajectoryPlayer = loadMatchingTrajectoryForSplat || disableTrajectoryPlayerInput
                ? CameraTrajectoryPlayer.FindOrCreate()
                : FindAnyObjectByType<CameraTrajectoryPlayer>();
        if (locomotion == null)
            locomotion = FindAnyObjectByType<VRLocomotion>();
        if (splatRotator == null)
            splatRotator = FindAnyObjectByType<SplatRotator>();
        if (focusBlur == null)
            focusBlur = FindAnyObjectByType<WorldFocusBlurController>();
        if (rig == null)
            rig = FindAnyObjectByType<VRRig>();
        if (targetCamera == null)
            targetCamera = rig != null && rig.xrCamera != null ? rig.xrCamera : Camera.main;
    }

    void BuildStudyStimuli()
    {
        EnsureStimuli();
        _studyStimuli.Clear();

        if (HasOrderedSplatAssets())
        {
            for (int i = 0; i < orderedSplatAssets.Count; i++)
            {
                UnityEngine.Object asset = orderedSplatAssets[i];
                if (asset == null)
                    continue;

                var stimulus = new StudyStimulus
                {
                    label = asset.name,
                    splatAsset = asset,
                    splatPath = ResolveSplatAssetPath(asset),
                    photoPath = ""
                };

                if (IsStudyStimulusLoadable(stimulus, i))
                    _studyStimuli.Add(stimulus);
            }

            return;
        }

        for (int i = 0; i < stimuli.Count; i++)
        {
            if (IsStudyStimulusLoadable(stimuli[i], i))
                _studyStimuli.Add(stimuli[i]);
        }
    }

    bool IsStudyStimulusLoadable(StudyStimulus stimulus, int sourceIndex)
    {
        if (stimulus == null)
        {
            Debug.LogWarning($"[UserStudyFlowController] Skipping empty asset entry at index {sourceIndex}.");
            return false;
        }

        if (stimulus.splatAsset is GaussianSplatAsset)
            return true;

        string splatPath = ResolveSplatPath(stimulus);
        string label = ResolveStimulusLabel(stimulus, sourceIndex);
        if (string.IsNullOrWhiteSpace(splatPath))
        {
            Debug.LogWarning($"[UserStudyFlowController] Skipping asset {label}: missing splat path.");
            return false;
        }

        if (File.Exists(splatPath))
            return true;

        Debug.LogWarning($"[UserStudyFlowController] Skipping asset {label}: splat file not found: {splatPath}");
        return false;
    }

    void EnsureStimuli()
    {
        if (HasOrderedSplatAssets() || stimuli.Count > 0 || !autoScanStimulusFolderWhenEmpty)
            return;

        string folder = ResolveFolderPath(stimulusFolder);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return;

        string[] files = Directory.GetFiles(folder)
            .Where(RuntimeSplatLoader.IsSupportedFileExtension)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        for (int i = 0; i < files.Length; i++)
        {
            stimuli.Add(new StudyStimulus
            {
                label = Path.GetFileNameWithoutExtension(files[i]),
                splatAsset = null,
                splatPath = files[i],
                photoPath = ""
            });
        }
    }

    void BuildRuntimeSequence()
    {
        _runtimeSequence.Clear();

        for (int i = 0; i < _studyStimuli.Count; i++)
            _runtimeSequence.Add(i);

        _activeSequenceLabel = "configured";
    }

    void RotateSequence(int offset)
    {
        if (_runtimeSequence.Count <= 1 || offset == 0)
            return;

        var rotated = new List<int>(_runtimeSequence.Count);
        for (int i = 0; i < _runtimeSequence.Count; i++)
            rotated.Add(_runtimeSequence[(i + offset) % _runtimeSequence.Count]);
        _runtimeSequence.Clear();
        _runtimeSequence.AddRange(rotated);
    }

    void ShuffleSequence(int seed)
    {
        var random = new System.Random(PositiveModulo(seed, int.MaxValue));
        for (int i = _runtimeSequence.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            int temp = _runtimeSequence[i];
            _runtimeSequence[i] = _runtimeSequence[j];
            _runtimeSequence[j] = temp;
        }
    }

    void ShowParticipantPrompt()
    {
        if (focusBlur != null)
            focusBlur.effectEnabled = false;

        _participantPromptVisible = true;
        if (_participantPanelRoot != null)
            _participantPanelRoot.SetActive(true);
        UpdateParticipantPromptText();
        PlaceParticipantPanelInFront();
    }

    void HideParticipantPrompt()
    {
        _participantPromptVisible = false;
        if (_participantPanelRoot != null)
            _participantPanelRoot.SetActive(false);
    }

    void ShowLoadingPanel(string title, string detail)
    {
        if (!showLoadingPanel)
            return;

        BuildLoadingPanel();

        if (_loadingTitleText != null)
            _loadingTitleText.text = string.IsNullOrWhiteSpace(title) ? "Loading" : title;
        if (_loadingDetailText != null)
            _loadingDetailText.text = detail ?? string.Empty;

        if (_loadingPanelRoot != null)
        {
            _loadingPanelRoot.SetActive(true);
            PlaceLoadingPanelInFront();
        }

        Canvas.ForceUpdateCanvases();
    }

    void HideLoadingPanel()
    {
        if (_loadingPanelRoot != null)
            _loadingPanelRoot.SetActive(false);
    }

    string ResolveCurrentLoadingDetail()
    {
        if (_currentSequencePosition < 0 || _currentSequencePosition >= _runtimeSequence.Count)
            return string.Empty;

        int stimulusIndex = _runtimeSequence[_currentSequencePosition];
        if (stimulusIndex < 0 || stimulusIndex >= _studyStimuli.Count)
            return string.Empty;

        StudyStimulus stimulus = _studyStimuli[stimulusIndex];
        string label = ResolveStimulusLabel(stimulus, stimulusIndex);
        string mode = ((int)_currentMode).ToString() + " - " + _currentMode;
        return label + "\nMode " + mode;
    }

    void UpdateParticipantPromptText()
    {
        if (_participantTitleText != null)
            _participantTitleText.text = "Participant ID";
        if (_participantValueText != null)
            _participantValueText.text = string.IsNullOrEmpty(participantId) ? "_" : participantId + "_";
        if (_participantHintText != null)
            _participantHintText.text = "Type ID and press Enter";
    }

    void BuildParticipantPanel()
    {
        if (_participantPanelRoot != null)
            return;

        _participantPanelRoot = new GameObject("UserStudyParticipantPrompt");
        var canvas = _participantPanelRoot.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 100;

        RectTransform rootRect = _participantPanelRoot.GetComponent<RectTransform>();
        rootRect.sizeDelta = new Vector2(900f, 280f);
        _participantPanelRoot.transform.localScale = Vector3.one * 0.001f;

        var bg = MakeUiChild(_participantPanelRoot.transform, "Background");
        var bgImage = bg.AddComponent<Image>();
        bgImage.color = new Color(0.03f, 0.035f, 0.04f, 0.96f);
        Stretch(bg.GetComponent<RectTransform>());

        _participantTitleText = MakeText(bg.transform, "Title", 44, Color.white, TextAnchor.MiddleCenter);
        SetRect(_participantTitleText.rectTransform, 0f, -35f, 820f, 70f);

        _participantValueText = MakeText(bg.transform, "Value", 38, new Color(0.85f, 0.95f, 1f, 1f), TextAnchor.MiddleCenter);
        SetRect(_participantValueText.rectTransform, 0f, -120f, 820f, 62f);

        _participantHintText = MakeText(bg.transform, "Hint", 22, new Color(0.68f, 0.72f, 0.76f, 1f), TextAnchor.MiddleCenter);
        SetRect(_participantHintText.rectTransform, 0f, -205f, 820f, 40f);

        _participantPanelRoot.SetActive(false);
    }

    void BuildLoadingPanel()
    {
        if (_loadingPanelRoot != null)
            return;

        _loadingPanelRoot = new GameObject("UserStudyLoadingPanel");
        var canvas = _loadingPanelRoot.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 130;

        RectTransform rootRect = _loadingPanelRoot.GetComponent<RectTransform>();
        rootRect.sizeDelta = new Vector2(760f, 260f);
        _loadingPanelRoot.transform.localScale = Vector3.one * 0.001f;

        var bg = MakeUiChild(_loadingPanelRoot.transform, "Background");
        var bgImage = bg.AddComponent<Image>();
        bgImage.color = new Color(0.02f, 0.025f, 0.03f, 0.97f);
        Stretch(bg.GetComponent<RectTransform>());

        var accent = MakeUiChild(bg.transform, "Accent");
        var accentImage = accent.AddComponent<Image>();
        accentImage.color = new Color(0.35f, 0.62f, 1f, 1f);
        SetRect(accent.GetComponent<RectTransform>(), 0f, -6f, 760f, 12f);

        _loadingTitleText = MakeText(bg.transform, "Title", 42, Color.white, TextAnchor.MiddleCenter);
        SetRect(_loadingTitleText.rectTransform, 0f, -78f, 700f, 62f);

        _loadingDetailText = MakeText(bg.transform, "Detail", 24, new Color(0.78f, 0.84f, 0.90f, 1f), TextAnchor.MiddleCenter);
        SetRect(_loadingDetailText.rectTransform, 0f, -162f, 700f, 86f);

        _loadingPanelRoot.SetActive(false);
    }

    void BuildPhotoScreen()
    {
        if (_photoScreenRoot != null)
            return;

        _photoScreenRoot = new GameObject("UserStudyPhotoScreen");
        var canvas = _photoScreenRoot.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 10;

        _photoCanvasRect = _photoScreenRoot.GetComponent<RectTransform>();
        _photoCanvasRect.sizeDelta = new Vector2(1200f, 675f);
        _photoScreenRoot.transform.localScale = Vector3.one * (photoScreenWidthMeters / 1200f);

        var bg = MakeUiChild(_photoScreenRoot.transform, "Background");
        var bgImage = bg.AddComponent<Image>();
        bgImage.color = Color.black;
        Stretch(bg.GetComponent<RectTransform>());

        var imageGo = MakeUiChild(bg.transform, "Photo");
        _photoImage = imageGo.AddComponent<RawImage>();
        _photoImage.color = Color.white;
        Stretch(imageGo.GetComponent<RectTransform>());

        _photoMissingText = MakeText(bg.transform, "MissingPhoto", 36, new Color(0.85f, 0.85f, 0.85f, 1f), TextAnchor.MiddleCenter);
        Stretch(_photoMissingText.rectTransform);
        _photoMissingText.gameObject.SetActive(false);

        _photoScreenRoot.SetActive(false);
    }

    void ShowPhotoScreen(bool visible)
    {
        if (_photoScreenRoot != null)
            _photoScreenRoot.SetActive(visible);
    }

    void ResizePhotoScreen(int width, int height)
    {
        if (_photoCanvasRect == null || width <= 0 || height <= 0)
            return;

        const float baseWidth = 1200f;
        float aspect = (float)height / width;
        _photoCanvasRect.sizeDelta = new Vector2(baseWidth, Mathf.Max(1f, baseWidth * aspect));
        _photoScreenRoot.transform.localScale = Vector3.one * (photoScreenWidthMeters / baseWidth);
    }

    void PlaceParticipantPanelInFront()
    {
        if (_participantPanelRoot == null)
            return;

        Camera camera = ResolveTargetCamera();
        if (camera == null)
            return;

        Vector3 forward = camera.transform.forward.sqrMagnitude > 0.001f ? camera.transform.forward.normalized : Vector3.forward;
        _participantPanelRoot.transform.position = camera.transform.position + forward * 1.5f;
        _participantPanelRoot.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
    }

    void PlaceLoadingPanelInFront()
    {
        if (_loadingPanelRoot == null)
            return;

        Camera camera = ResolveTargetCamera();
        if (camera == null)
            return;

        Vector3 forward = camera.transform.forward.sqrMagnitude > 0.001f ? camera.transform.forward.normalized : Vector3.forward;
        _loadingPanelRoot.transform.position = camera.transform.position + forward * Mathf.Max(0.25f, loadingPanelDistance);
        _loadingPanelRoot.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
    }

    void PlacePhotoScreenInFront()
    {
        if (_photoScreenRoot == null)
            return;

        Camera camera = ResolveTargetCamera();
        if (camera == null)
            return;

        Vector3 forward = camera.transform.forward.sqrMagnitude > 0.001f ? camera.transform.forward.normalized : Vector3.forward;
        _photoScreenRoot.transform.position = camera.transform.position + forward * Mathf.Max(0.25f, photoScreenDistance);
        _photoScreenRoot.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
    }

    Camera ResolveTargetCamera()
    {
        if (targetCamera == null)
            ResolveReferences();
        return targetCamera;
    }

    bool TryLoadMatchingTrajectory(string splatPath)
    {
        if (!loadMatchingTrajectoryForSplat)
            return false;

        if (trajectoryPlayer == null)
            trajectoryPlayer = CameraTrajectoryPlayer.FindOrCreate();

        if (trajectoryPlayer == null)
            return false;

        if (string.IsNullOrWhiteSpace(splatPath))
        {
            trajectoryPlayer.ClearTrajectory();
            return false;
        }

        return trajectoryPlayer.TryLoadMatchingTrajectoryForSplat(splatPath, true);
    }

    string ResolvePhotoPath(StudyStimulus stimulus)
    {
        string splatPath = ResolveSplatPath(stimulus);
        string matchedPath = ResolveMatchingPhotoPath(splatPath);
        if (!string.IsNullOrWhiteSpace(matchedPath))
            return matchedPath;

        string explicitPath = ResolveStudyPath(stimulus.photoPath);
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
            return explicitPath;

        return explicitPath;
    }

    string ResolveMatchingPhotoPath(string splatPath)
    {
        if (string.IsNullOrWhiteSpace(splatPath))
            return null;

        string folder = Path.GetDirectoryName(splatPath);
        string baseName = Path.GetFileNameWithoutExtension(splatPath);
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(baseName))
            return null;

        string[] candidates =
        {
            Path.Combine(folder, baseName + ".png"),
            Path.Combine(folder, baseName + ".jpg"),
            Path.Combine(folder, baseName + ".jpeg")
        };

        for (int i = 0; i < candidates.Length; i++)
        {
            if (File.Exists(candidates[i]))
                return candidates[i];
        }

        return null;
    }

    string ResolveSplatPath(StudyStimulus stimulus)
    {
        if (!string.IsNullOrWhiteSpace(stimulus.splatPath))
            return ResolveStudyPath(stimulus.splatPath);

        if (stimulus.splatAsset != null)
            return ResolveSplatAssetPath(stimulus.splatAsset);

        return null;
    }

    string ResolveSplatAssetPath(UnityEngine.Object asset)
    {
        if (asset == null)
            return null;

#if UNITY_EDITOR
        string assetPath = UnityEditor.AssetDatabase.GetAssetPath(asset);
        if (!string.IsNullOrWhiteSpace(assetPath))
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
        }
#endif

        return asset.name;
    }

    string ResolveStudyPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (Path.IsPathRooted(path))
            return path;

        string fromStimulusFolder = null;
        string folder = ResolveFolderPath(stimulusFolder);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            fromStimulusFolder = Path.Combine(folder, path);
            if (File.Exists(fromStimulusFolder))
                return fromStimulusFolder;
        }

        string fromAssets = Path.Combine(Application.dataPath, path);
        if (File.Exists(fromAssets))
            return fromAssets;

        string fromCwd = Path.GetFullPath(path);
        if (File.Exists(fromCwd))
            return fromCwd;

        return !string.IsNullOrWhiteSpace(fromStimulusFolder) ? fromStimulusFolder : fromCwd;
    }

    string ResolveFolderPath(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return null;

        if (Path.IsPathRooted(folder))
            return folder;

        string fromAssets = Path.Combine(Application.dataPath, folder);
        if (Directory.Exists(fromAssets))
            return fromAssets;

        string fromCwd = Path.GetFullPath(folder);
        return Directory.Exists(fromCwd) ? fromCwd : fromAssets;
    }

    string ResolveStimulusLabel(StudyStimulus stimulus, int index)
    {
        if (!string.IsNullOrWhiteSpace(stimulus.label))
            return stimulus.label;

        if (stimulus.splatAsset != null)
            return stimulus.splatAsset.name;

        string path = !string.IsNullOrWhiteSpace(stimulus.splatPath) ? stimulus.splatPath : stimulus.photoPath;
        if (!string.IsNullOrWhiteSpace(path))
            return Path.GetFileNameWithoutExtension(path);

        return "asset_" + (index + 1).ToString();
    }

    bool HasOrderedSplatAssets()
    {
        return orderedSplatAssets != null && orderedSplatAssets.Any(asset => asset != null);
    }

    static GameObject MakeUiChild(Transform parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        return go;
    }

    static Text MakeText(Transform parent, string name, int fontSize, Color color, TextAnchor anchor)
    {
        var go = MakeUiChild(parent, name);
        var text = go.AddComponent<Text>();
        text.font = ResolveFont();
        text.fontSize = fontSize;
        text.color = color;
        text.alignment = anchor;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    static Font ResolveFont()
    {
        if (s_Font != null)
            return s_Font;

        s_Font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (s_Font == null)
            s_Font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        if (s_Font == null)
            s_Font = Font.CreateDynamicFontFromOSFont("Arial", 16);
        return s_Font;
    }

    static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    static void SetRect(RectTransform rect, float centerX, float centerY, float width, float height)
    {
        rect.anchorMin = new Vector2(0.5f, 1f);
        rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = new Vector2(centerX, centerY);
        rect.sizeDelta = new Vector2(width, height);
    }

    static int StableHash(string value)
    {
        unchecked
        {
            int hash = (int)2166136261;
            string text = value ?? "";
            for (int i = 0; i < text.Length; i++)
            {
                hash ^= char.ToUpperInvariant(text[i]);
                hash *= 16777619;
            }
            return hash;
        }
    }

    static int PositiveModulo(int value, int divisor)
    {
        if (divisor <= 0)
            return 0;
        int result = value % divisor;
        return result < 0 ? result + divisor : result;
    }

    static bool PathsEqual(string lhs, string rhs)
    {
        return string.Equals(lhs, rhs, StringComparison.OrdinalIgnoreCase);
    }
}
