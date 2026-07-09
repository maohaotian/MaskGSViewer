// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using GaussianSplatting.Runtime;
using Unity.Mathematics;
using UnityEngine;
using WebP;

/// <summary>
/// Loads .ply, .spz, .sog, and .spx Gaussian Splat files at runtime,
/// creating a GaussianSplatAsset in memory and assigning it to a GaussianSplatRenderer.
/// Uses Float32 quality (no chunking) for maximum simplicity.
/// </summary>
public class RuntimeSplatLoader : MonoBehaviour
{
    [Tooltip("The GaussianSplatRenderer to load splats into.")]
    public GaussianSplatRenderer targetRenderer;

    GaussianSplatAsset _currentAsset;
    string _currentFilePath;
    readonly object _preloadLock = new object();
    readonly Dictionary<string, GaussianSplatAsset> _preloadedAssets = new Dictionary<string, GaussianSplatAsset>(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _desiredPreloadPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _preloadQueue = new List<string>();
    Task<PreparedRuntimeAsset> _activePreloadTask;
    string _activePreloadPath;
    long _preloadCachedBytes;
    long _preloadBudgetBytes;

    /// <summary>Total bytes currently used by preloaded assets in RAM.</summary>
    public long PreloadCachedBytes { get { lock (_preloadLock) return _preloadCachedBytes; } }

    /// <summary>Absolute path of the currently loaded splat file, if any.</summary>
    public string CurrentFilePath => _currentFilePath;

    /// <summary>Maximum bytes allowed for the preload cache (0 = unlimited).</summary>
    public long PreloadBudgetBytes { get { lock (_preloadLock) return _preloadBudgetBytes; } }

    sealed class PreparedRuntimeAsset
    {
        public string filePath;
        public string assetName;
        public int splatCount;
        public float3 boundsMin;
        public float3 boundsMax;
        public byte[] posData;
        public byte[] otherData;
        public byte[] colorData;
        public byte[] shData;

        public long TotalBytes => (long)(posData?.Length ?? 0) + (otherData?.Length ?? 0) + (colorData?.Length ?? 0) + (shData?.Length ?? 0);
    }

    void Awake()
    {
        if (targetRenderer == null)
            targetRenderer = GetComponent<GaussianSplatRenderer>();
    }

    /// <summary>
    /// Set the RAM budget for preloading, as a fraction of total system RAM.
    /// E.g. 0.3 means 30% of system RAM. Pass 0 to disable budget limit.
    /// </summary>
    public void SetPreloadBudgetFraction(float fraction)
    {
        long totalRam = (long)SystemInfo.systemMemorySize * 1024L * 1024L; // MB → bytes
        lock (_preloadLock)
        {
            _preloadBudgetBytes = fraction > 0f ? (long)(totalRam * Mathf.Clamp01(fraction)) : 0;
        }
        Debug.Log($"[RuntimeSplatLoader] Preload budget set to {_preloadBudgetBytes / (1024 * 1024)}MB ({fraction:P0} of {SystemInfo.systemMemorySize}MB RAM)");
    }

    /// <summary>
    /// Estimate the in-memory size (bytes) of a splat file once decoded to Float32 quality.
    /// Uses file size as a heuristic since the exact splat count isn't known without reading.
    /// </summary>
    public static long EstimateAssetBytes(string filePath)
    {
        return SplatInputAdapters.EstimateRuntimeBytes(filePath);
    }

    public static bool IsSupportedFileExtension(string filePath)
    {
        return SplatInputAdapters.IsSupportedFileExtension(filePath);
    }

    public void SetPreloadTargets(IEnumerable<string> filePaths)
    {
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (filePaths != null)
        {
            foreach (string filePath in filePaths)
            {
                if (!string.IsNullOrWhiteSpace(filePath))
                    desired.Add(filePath);
            }
        }

        List<GaussianSplatAsset> staleAssets = null;
        List<string> staleAssetPaths = null;

        lock (_preloadLock)
        {
            _desiredPreloadPaths.Clear();
            foreach (string filePath in desired)
                _desiredPreloadPaths.Add(filePath);

            foreach (var kvp in _preloadedAssets)
            {
                if (_desiredPreloadPaths.Contains(kvp.Key) || PathsEqual(kvp.Key, _currentFilePath))
                    continue;

                staleAssets ??= new List<GaussianSplatAsset>();
                staleAssetPaths ??= new List<string>();
                staleAssets.Add(kvp.Value);
                staleAssetPaths.Add(kvp.Key);
            }
            if (staleAssetPaths != null)
            {
                foreach (string filePath in staleAssetPaths)
                {
                    _preloadedAssets.Remove(filePath);
                    _preloadCachedBytes -= EstimateAssetBytes(filePath);
                }
                if (_preloadCachedBytes < 0) _preloadCachedBytes = 0;
            }

            // Remove queued entries that are no longer desired
            _preloadQueue.RemoveAll(p => !_desiredPreloadPaths.Contains(p));
        }

        if (staleAssets != null)
        {
            foreach (var asset in staleAssets)
            {
                if (asset != null)
                    Destroy(asset);
            }
        }
    }

    public void BeginPreload(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath) || !IsSupportedFileExtension(filePath))
            return;

        lock (_preloadLock)
        {
            if (_preloadedAssets.ContainsKey(filePath) || PathsEqual(filePath, _currentFilePath))
                return;
            if (PathsEqual(filePath, _activePreloadPath))
                return;
            if (_preloadQueue.FindIndex(p => PathsEqual(p, filePath)) >= 0)
                return;

            _preloadQueue.Add(filePath);
        }
    }

    void StartNextPreload()
    {
        lock (_preloadLock)
        {
            if (_activePreloadTask != null)
                return;

            while (_preloadQueue.Count > 0)
            {
                string next = _preloadQueue[0];
                _preloadQueue.RemoveAt(0);

                // Skip if already cached, already current, or no longer desired
                if (_preloadedAssets.ContainsKey(next) || PathsEqual(next, _currentFilePath) || !_desiredPreloadPaths.Contains(next))
                    continue;

                // Check RAM budget before starting
                if (_preloadBudgetBytes > 0)
                {
                    long estimatedCost = EstimateAssetBytes(next);
                    if (_preloadCachedBytes + estimatedCost > _preloadBudgetBytes)
                    {
                        Debug.Log($"[RuntimeSplatLoader] Preload SKIPPED \"{Path.GetFileName(next)}\" — budget full ({_preloadCachedBytes / (1024 * 1024)}MB / {_preloadBudgetBytes / (1024 * 1024)}MB)");
                        continue;
                    }
                }

                _activePreloadPath = next;
                string fileName = Path.GetFileName(next);
                Debug.Log($"[RuntimeSplatLoader] Preload STARTED for \"{fileName}\"");
                _activePreloadTask = Task.Run(() =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var result = BuildPreparedRuntimeAsset(next);
                    Debug.Log($"[RuntimeSplatLoader] Preload background work DONE for \"{fileName}\" in {sw.ElapsedMilliseconds}ms");
                    return result;
                });
                return;
            }
        }
    }

    public void PumpCompletedPreloads(int maxToFinalize = 1)
    {
        // Check if the active background task has finished
        Task<PreparedRuntimeAsset> finishedTask = null;
        string finishedPath = null;

        lock (_preloadLock)
        {
            if (_activePreloadTask != null && _activePreloadTask.IsCompleted)
            {
                finishedTask = _activePreloadTask;
                finishedPath = _activePreloadPath;
                _activePreloadTask = null;
                _activePreloadPath = null;
            }
        }

        if (finishedTask != null)
        {
            PreparedRuntimeAsset prepared = null;
            Exception error = null;
            try
            {
                prepared = finishedTask.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                error = ex;
            }

            bool shouldKeep;
            lock (_preloadLock)
            {
                shouldKeep = _desiredPreloadPaths.Contains(finishedPath);
            }

            if (error != null)
            {
                Debug.LogWarning($"[RuntimeSplatLoader] Preload failed for {finishedPath}: {error.Message}");
            }
            else if (shouldKeep && prepared != null)
            {
                var asset = CreateRuntimeAsset(prepared);
                long assetBytes = prepared.TotalBytes;
                bool keepAsset = false;
                lock (_preloadLock)
                {
                    if (_desiredPreloadPaths.Contains(finishedPath) && !_preloadedAssets.ContainsKey(finishedPath))
                    {
                        _preloadedAssets.Add(finishedPath, asset);
                        _preloadCachedBytes += assetBytes;
                        keepAsset = true;
                    }
                }

                if (keepAsset)
                    Debug.Log($"[RuntimeSplatLoader] Preload FINALIZED \"{Path.GetFileName(finishedPath)}\" into cache");
                else
                    Destroy(asset);
            }
        }

        // Start next queued preload if nothing is in flight
        StartNextPreload();
    }

    /// <summary>Load a .ply, .spz, or bundled .sog file from disk and display it.</summary>
    public bool LoadFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Debug.LogError($"[RuntimeSplatLoader] File not found: {filePath}");
            return false;
        }

        if (!IsSupportedFileExtension(filePath))
        {
            Debug.LogError($"[RuntimeSplatLoader] Unsupported format: {Path.GetExtension(filePath)}");
            return false;
        }

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var asset = GetOrCreateAsset(filePath);
            if (asset == null)
                return false;

            AssignCurrentAsset(filePath, asset);
            Debug.Log($"[RuntimeSplatLoader] Loaded \"{asset.name}\" ({asset.splatCount:N0} splats) in {sw.ElapsedMilliseconds}ms");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RuntimeSplatLoader] Failed to load {filePath}: {ex}");
            return false;
        }
    }


    // ── Movie Mode ──────────────────────────────────────────────────────────

    GaussianSplatAsset[] _movieFrames;

    /// <summary>True while movie frames are loaded and ready.</summary>
    public bool IsMovieReady => _movieFrames != null && _movieFrames.Length > 0;

    /// <summary>Number of loaded movie frames.</summary>
    public int MovieFrameCount => _movieFrames?.Length ?? 0;

    /// <summary>
    /// Estimate total RAM needed to load all files. Returns bytes.
    /// </summary>
    public static long EstimateTotalBytes(IReadOnlyList<string> files)
    {
        long total = 0;
        for (int i = 0; i < files.Count; i++)
            total += EstimateAssetBytes(files[i]);
        return total;
    }

    /// <summary>
    /// Check whether there is enough free system RAM (estimated) to load all files.
    /// Returns (fitsInRam, estimatedBytes, availableBytes).
    /// </summary>
    public static (bool fits, long estimatedBytes, long availableBytes) CheckMovieRamFit(IReadOnlyList<string> files)
    {
        long estimated = EstimateTotalBytes(files);
        // Use 80% of total system RAM as the ceiling
        long available = (long)SystemInfo.systemMemorySize * 1024L * 1024L * 80L / 100L;
        return (estimated <= available, estimated, available);
    }

    /// <summary>
    /// Progressively load all files into movie frames on a background thread.
    /// Call PumpMovieLoad() each frame to finalize completed frames.
    /// The onProgress callback receives (loadedCount, totalCount).
    /// Returns false if loading cannot start.
    /// </summary>
    public bool BeginMovieLoad(IReadOnlyList<string> files, Action<int, int> onProgress)
    {
        StopMovie();

        _movieLoadFiles = new List<string>(files);
        _movieLoadTotal = files.Count;
        _movieLoadDone = 0;
        _movieLaunchNext = 0;
        _movieLoadFailed = false;
        _movieLoadProgress = onProgress;
        _movieLoadResults = new GaussianSplatAsset[files.Count];
        _movieLoadTasks = new Task<PreparedRuntimeAsset>[files.Count];
        _movieLoadStopwatch = System.Diagnostics.Stopwatch.StartNew();
        _movieLastError = null;

        _movieMaxConcurrency = DetermineMovieConcurrency(files);

        // Pre-launch up to 2x concurrency so the thread-pool never starves
        // while we wait for in-order finalization.
        int initialBatch = Math.Min(_movieMaxConcurrency * 2, files.Count);
        for (int i = 0; i < initialBatch; i++)
            LaunchMovieTask(i);
        _movieLaunchNext = initialBatch;

        Debug.Log($"[RuntimeSplatLoader] Movie decode started: {files.Count} frames, {_movieMaxConcurrency} parallel workers");
        return true;
    }

    void LaunchMovieTask(int index)
    {
        string filePath = _movieLoadFiles[index];
        string fileName = Path.GetFileName(filePath);
        _movieLoadTasks[index] = Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = BuildPreparedRuntimeAsset(filePath);
            Debug.Log($"[RuntimeSplatLoader] Movie frame \"{fileName}\" decoded in {sw.ElapsedMilliseconds}ms");
            return result;
        });
    }

    /// <summary>
    /// Pump movie loading each frame. Returns true when all frames are loaded.
    /// </summary>
    public bool PumpMovieLoad()
    {
        if (_movieLoadFiles == null)
            return true; // nothing to do

        // Finalize as many completed frames as possible (in order)
        while (_movieLoadDone < _movieLoadTotal)
        {
            var task = _movieLoadTasks[_movieLoadDone];
            if (!task.IsCompleted) break;

            PreparedRuntimeAsset prepared = null;
            try
            {
                prepared = task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                string failedPath = _movieLoadFiles[_movieLoadDone];
                Debug.LogWarning($"[RuntimeSplatLoader] Movie worker failed for frame {_movieLoadDone} ({Path.GetFileName(failedPath)}): {ex.Message}. Retrying synchronously.");

                try
                {
                    prepared = BuildPreparedRuntimeAsset(failedPath);
                    Debug.Log($"[RuntimeSplatLoader] Movie retry succeeded for frame {_movieLoadDone} ({Path.GetFileName(failedPath)})");
                }
                catch (Exception retryEx)
                {
                    _movieLastError = $"{Path.GetFileName(failedPath)}: {retryEx.Message}";
                    Debug.LogError($"[RuntimeSplatLoader] Movie load failed for frame {_movieLoadDone} ({Path.GetFileName(failedPath)}): {retryEx.Message}");
                    _movieLoadFailed = true;
                }
            }

            if (prepared != null && !_movieLoadFailed)
            {
                _movieLoadResults[_movieLoadDone] = CreateRuntimeAsset(prepared);
            }

            _movieLoadDone++;
            _movieLoadProgress?.Invoke(_movieLoadDone, _movieLoadTotal);

            if (_movieLoadFailed) break;
        }

        // Top-up the worker pool independently of finalization order.
        // This avoids stalling launches when an early frame is slow but
        // later frames have already completed.
        if (!_movieLoadFailed)
        {
            int maxInFlight = _movieMaxConcurrency * 2;
            while (_movieLaunchNext < _movieLoadTotal &&
                   _movieLaunchNext - _movieLoadDone < maxInFlight)
            {
                LaunchMovieTask(_movieLaunchNext);
                _movieLaunchNext++;
            }
        }

        if (_movieLoadFailed || _movieLoadDone >= _movieLoadTotal)
        {
            // Finished (or failed)
            if (!_movieLoadFailed)
            {
                _movieFrames = _movieLoadResults;
                _movieLoadStopwatch?.Stop();
                long elapsedMs = _movieLoadStopwatch?.ElapsedMilliseconds ?? 0;
                float elapsedSec = elapsedMs / 1000f;
                float avgMs = _movieFrames.Length > 0 ? (float)elapsedMs / _movieFrames.Length : 0;
                Debug.Log($"[RuntimeSplatLoader] Movie loaded: {_movieFrames.Length} frames in {elapsedSec:F1}s ({avgMs:F0}ms/frame avg, {_movieMaxConcurrency} workers)");
            }
            else
            {
                // Clean up partial results
                for (int i = 0; i < _movieLoadResults.Length; i++)
                {
                    if (_movieLoadResults[i] != null)
                        Destroy(_movieLoadResults[i]);
                }
                _movieLoadResults = null;
                if (!string.IsNullOrEmpty(_movieLastError))
                    Debug.LogError($"[RuntimeSplatLoader] Movie load aborted: {_movieLastError}");
            }

            _movieLoadFiles = null;
            _movieLoadTasks = null;
            _movieLoadProgress = null;
            return true;
        }

        return false;
    }

    /// <summary>Show a specific movie frame by index (instant swap, no loading).</summary>
    public void ShowMovieFrame(int frameIndex)
    {
        if (_movieFrames == null || frameIndex < 0 || frameIndex >= _movieFrames.Length)
            return;

        var asset = _movieFrames[frameIndex];
        if (asset == null) return;

        _currentAsset = asset;
        _currentFilePath = null;
        targetRenderer.m_Asset = asset;
    }

    /// <summary>Release all movie frames from RAM.</summary>
    public void StopMovie()
    {
        if (_movieFrames != null)
        {
            for (int i = 0; i < _movieFrames.Length; i++)
            {
                if (_movieFrames[i] != null && !ReferenceEquals(_movieFrames[i], _currentAsset))
                    Destroy(_movieFrames[i]);
            }
            _movieFrames = null;
            Debug.Log("[RuntimeSplatLoader] Movie frames released");
        }

        // Cancel any in-progress movie load
        _movieLoadFiles = null;
        _movieLoadTasks = null;
        _movieLoadResults = null;
        _movieLoadProgress = null;
        _movieLastError = null;
    }

    public string MovieLastError => _movieLastError;

    static int DetermineMovieConcurrency(IReadOnlyList<string> files)
    {
        // Use all available cores. The native WebP decoder is thread-safe
        // for independent calls, and file I/O + morton + packing all
        // benefit from full parallelism.
        return Math.Max(2, Environment.ProcessorCount);
    }

    internal static byte[] DecodeWebPImage(byte[] webpBytes, out int width, out int height, string debugName)
    {
        // libwebp native decode is thread-safe for independent calls –
        // each invocation allocates its own decoder, so no lock needed.
        Texture2DExt.GetWebPDimensions(webpBytes, out width, out height);
        Error error;
        byte[] rgba = Texture2DExt.LoadRGBAFromWebP(webpBytes, ref width, ref height, false, out error);
        if (error != Error.Success || rgba == null)
            throw new Exception($"Failed to decode WebP image {debugName}: {error}");

        return rgba;
    }

    // Movie loading state
    List<string> _movieLoadFiles;
    GaussianSplatAsset[] _movieLoadResults;
    Task<PreparedRuntimeAsset>[] _movieLoadTasks;
    Action<int, int> _movieLoadProgress;
    int _movieLoadTotal;
    int _movieLoadDone;
    int _movieLaunchNext;
    int _movieMaxConcurrency;
    bool _movieLoadFailed;
    System.Diagnostics.Stopwatch _movieLoadStopwatch;
    string _movieLastError;

    // ── End Movie Mode ────────────────────────────────────────────────────────


    void OnDestroy()
    {
        StopMovie();

        var toDestroy = new List<GaussianSplatAsset>();
        lock (_preloadLock)
        {
            foreach (var asset in _preloadedAssets.Values)
            {
                if (asset != null && !toDestroy.Contains(asset))
                    toDestroy.Add(asset);
            }
            _preloadedAssets.Clear();
            _preloadQueue.Clear();
            _desiredPreloadPaths.Clear();
            _activePreloadTask = null;
            _activePreloadPath = null;
            _preloadCachedBytes = 0;
        }

        if (_currentAsset != null && !toDestroy.Contains(_currentAsset))
            toDestroy.Add(_currentAsset);

        foreach (var asset in toDestroy)
        {
            if (asset != null)
                Destroy(asset);
        }
    }

    GaussianSplatAsset GetOrCreateAsset(string filePath)
    {
        string fileName = Path.GetFileName(filePath);
        GaussianSplatAsset cachedAsset;
        lock (_preloadLock)
        {
            if (_preloadedAssets.TryGetValue(filePath, out cachedAsset))
            {
                Debug.Log($"[RuntimeSplatLoader] CACHE HIT for \"{fileName}\" — returning preloaded asset");
                return cachedAsset;
            }
        }

        // Check if this file is the one currently being preloaded
        Task<PreparedRuntimeAsset> activeTask = null;
        lock (_preloadLock)
        {
            if (PathsEqual(filePath, _activePreloadPath) && _activePreloadTask != null)
            {
                activeTask = _activePreloadTask;
                _activePreloadTask = null;
                _activePreloadPath = null;
            }
        }

        PreparedRuntimeAsset prepared;
        if (activeTask != null)
        {
            bool alreadyDone = activeTask.IsCompleted;
            Debug.Log($"[RuntimeSplatLoader] TASK WAIT for \"{fileName}\" (already completed: {alreadyDone})");
            prepared = activeTask.GetAwaiter().GetResult();
        }
        else
        {
            Debug.Log($"[RuntimeSplatLoader] SYNC BUILD for \"{fileName}\" — no preload available");
            prepared = BuildPreparedRuntimeAsset(filePath);
        }

        if (prepared == null)
            return null;

        var asset = CreateRuntimeAsset(prepared);
        bool cacheAsset;
        GaussianSplatAsset duplicateAsset = null;
        lock (_preloadLock)
        {
            cacheAsset = _desiredPreloadPaths.Contains(filePath);
            if (cacheAsset && !_preloadedAssets.ContainsKey(filePath))
                _preloadedAssets.Add(filePath, asset);
            else if (_preloadedAssets.TryGetValue(filePath, out cachedAsset))
            {
                cacheAsset = false;
                duplicateAsset = asset;
                asset = cachedAsset;
            }
        }

        if (duplicateAsset != null)
            Destroy(duplicateAsset);

        return asset;
    }

    static PreparedRuntimeAsset BuildPreparedRuntimeAsset(string filePath)
    {
        var swStep = System.Diagnostics.Stopwatch.StartNew();

        var splats = ReadInputSplats(filePath);
        if (splats == null || splats.Length == 0)
            return null;
        long msRead = swStep.ElapsedMilliseconds;

        swStep.Restart();
        MortonReorder(splats, out float3 bMin, out float3 bMax);
        long msMorton = swStep.ElapsedMilliseconds;

        swStep.Restart();
        var result = new PreparedRuntimeAsset
        {
            filePath = filePath,
            assetName = Path.GetFileNameWithoutExtension(filePath),
            splatCount = splats.Length,
            boundsMin = bMin,
            boundsMax = bMax,
            posData = PackPositions(splats),
            otherData = PackOther(splats),
            colorData = PackColor(splats),
            shData = PackSH(splats)
        };
        long msPack = swStep.ElapsedMilliseconds;

        Debug.Log($"[RuntimeSplatLoader] \"{Path.GetFileName(filePath)}\" breakdown: read={msRead}ms morton={msMorton}ms pack={msPack}ms ({splats.Length:N0} splats)");
        return result;
    }

    static SplatData[] ReadInputSplats(string filePath)
    {
        return SplatInputAdapters.ReadFile(filePath);
    }

    static GaussianSplatAsset CreateRuntimeAsset(PreparedRuntimeAsset prepared)
    {
        var asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
        asset.Initialize(
            prepared.splatCount,
            GaussianSplatAsset.VectorFormat.Float32,
            GaussianSplatAsset.VectorFormat.Float32,
            GaussianSplatAsset.ColorFormat.Float32x4,
            GaussianSplatAsset.SHFormat.Float32,
            (Vector3)prepared.boundsMin, (Vector3)prepared.boundsMax, null
        );
        asset.name = prepared.assetName;
        asset.runtimePosData = prepared.posData;
        asset.runtimeOtherData = prepared.otherData;
        asset.runtimeColorData = prepared.colorData;
        asset.runtimeSHData = prepared.shData;
        asset.SetDataHash(new Hash128((uint)prepared.splatCount, (uint)GaussianSplatAsset.kCurrentVersion, 0, (uint)prepared.filePath.GetHashCode()));
        return asset;
    }

    void AssignCurrentAsset(string filePath, GaussianSplatAsset asset)
    {
        if (_currentAsset != null && !ReferenceEquals(_currentAsset, asset) && !IsCachedAsset(_currentFilePath, _currentAsset))
            Destroy(_currentAsset);

        _currentAsset = asset;
        _currentFilePath = filePath;
        targetRenderer.m_Asset = _currentAsset;
    }

    bool IsCachedAsset(string filePath, GaussianSplatAsset asset)
    {
        if (string.IsNullOrWhiteSpace(filePath) || asset == null)
            return false;

        lock (_preloadLock)
        {
            return _preloadedAssets.TryGetValue(filePath, out var cachedAsset) && ReferenceEquals(cachedAsset, asset);
        }
    }

    static bool PathsEqual(string lhs, string rhs)
    {
        return string.Equals(lhs, rhs, StringComparison.OrdinalIgnoreCase);
    }

    // ── Runtime splat representation ─────────────────────────────────────────

    public struct SplatData
    {
        public float3 pos;
        public float3 dc0;     // color after SH0ToColor (ready for texture)
        public float opacity;  // [0,1] after Sigmoid (ready for texture)
        public float3 scale;   // linearized scale (after exp)
        public float4 rot;     // packed smallest-3 rotation from PackSmallest3Rotation
        public float3[] sh;    // SH bands 1-15, interleaved (R,G,B) per band (may be null)
    }

    // ── Morton Reorder ────────────────────────────────────────────────────────

    static void MortonReorder(SplatData[] splats, out float3 bMin, out float3 bMax)
    {
        // Compute bounds while building Morton keys (folds two O(N) passes into one)
        bMin = float.PositiveInfinity;
        bMax = float.NegativeInfinity;
        var keys = new ulong[splats.Length];
        var indices = new int[splats.Length];
        for (int i = 0; i < splats.Length; i++)
        {
            float3 p = splats[i].pos;
            bMin = math.min(bMin, p);
            bMax = math.max(bMax, p);
            indices[i] = i;
        }

        float3 inv = 1f / math.max(bMax - bMin, 1e-10f);
        float kScaler = (1 << 21) - 1;
        for (int i = 0; i < splats.Length; i++)
        {
            float3 norm = (splats[i].pos - bMin) * inv * kScaler;
            uint3 ipos = (uint3)math.clamp(norm, 0, kScaler);
            keys[i] = GaussianUtils.MortonEncode3(ipos);
        }

        Array.Sort(keys, indices);

        // In-place cycle permutation — avoids allocating a full SplatData[N] copy
        for (int i = 0; i < splats.Length; i++)
        {
            while (indices[i] != i)
            {
                int target = indices[i];
                (splats[i], splats[target]) = (splats[target], splats[i]);
                (indices[i], indices[target]) = (indices[target], target);
            }
        }
    }

    // ── Data Packing (Float32 / VeryHigh quality) ─────────────────────────────
    // Uses Buffer.BlockCopy for bulk float→byte conversion to avoid
    // per-field BitConverter.GetBytes allocations (was ~13M allocs per file).

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
    struct FloatUintUnion
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public float f;
        [System.Runtime.InteropServices.FieldOffset(0)] public uint u;
    }

    static byte[] PackPositions(SplatData[] splats)
    {
        int n = splats.Length;
        var buf = new float[n * 3];
        for (int i = 0; i < n; i++)
        {
            int o = i * 3;
            buf[o]     = splats[i].pos.x;
            buf[o + 1] = splats[i].pos.y;
            buf[o + 2] = splats[i].pos.z;
        }
        var data = new byte[n * 12];
        Buffer.BlockCopy(buf, 0, data, 0, data.Length);
        return data;
    }

    static byte[] PackOther(SplatData[] splats)
    {
        int n = splats.Length;
        // Layout per splat: uint32 rotation + float32×3 scale = 4 × uint32 = 16 bytes
        var buf = new uint[n * 4];
        FloatUintUnion fu;
        fu.u = 0;
        for (int i = 0; i < n; i++)
        {
            int o = i * 4;
            float4 r = splats[i].rot;
            buf[o] = (uint)(r.x * 1023.5f) |
                     ((uint)(r.y * 1023.5f) << 10) |
                     ((uint)(r.z * 1023.5f) << 20) |
                     ((uint)(r.w * 3.5f) << 30);
            fu.f = splats[i].scale.x; buf[o + 1] = fu.u;
            fu.f = splats[i].scale.y; buf[o + 2] = fu.u;
            fu.f = splats[i].scale.z; buf[o + 3] = fu.u;
        }
        var data = new byte[n * 16];
        Buffer.BlockCopy(buf, 0, data, 0, data.Length);
        return data;
    }

    static byte[] PackColor(SplatData[] splats)
    {
        var (width, height) = GaussianSplatAsset.CalcTextureSize(splats.Length);
        int pixelCount = width * height;
        var buf = new float[pixelCount * 4];
        for (int i = 0; i < splats.Length; i++)
        {
            int texIdx = SplatIndexToTextureIndex((uint)i);
            int o = texIdx * 4;
            buf[o]     = splats[i].dc0.x;
            buf[o + 1] = splats[i].dc0.y;
            buf[o + 2] = splats[i].dc0.z;
            buf[o + 3] = splats[i].opacity;
        }
        var data = new byte[pixelCount * 16];
        Buffer.BlockCopy(buf, 0, data, 0, data.Length);
        return data;
    }

    static byte[] PackSH(SplatData[] splats)
    {
        // SHTableItemFloat32: 15×float3 + float3 padding = 48 floats = 192 bytes
        const int floatsPerItem = 48;
        var buf = new float[splats.Length * floatsPerItem];
        for (int i = 0; i < splats.Length; i++)
        {
            if (splats[i].sh == null) continue;
            int baseOff = i * floatsPerItem;
            for (int band = 0; band < 15; band++)
            {
                int o = baseOff + band * 3;
                buf[o]     = splats[i].sh[band].x;
                buf[o + 1] = splats[i].sh[band].y;
                buf[o + 2] = splats[i].sh[band].z;
            }
        }
        var data = new byte[splats.Length * floatsPerItem * 4];
        Buffer.BlockCopy(buf, 0, data, 0, data.Length);
        return data;
    }

    // ── Morton texture tiling (matches GaussianSplatAssetCreator) ─────────────

    static int SplatIndexToTextureIndex(uint idx)
    {
        uint2 xy = GaussianUtils.DecodeMorton2D_16x16(idx & 0xFF);
        uint width = GaussianSplatAsset.kTextureWidth / 16;
        idx >>= 8;
        uint x = (idx % width) * 16 + xy.x;
        uint y = (idx / width) * 16 + xy.y;
        return (int)(y * GaussianSplatAsset.kTextureWidth + x);
    }
}
