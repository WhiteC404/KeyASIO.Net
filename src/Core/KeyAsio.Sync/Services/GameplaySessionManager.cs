using System.Runtime;
using System.Runtime.CompilerServices;
using Coosu.Beatmap;
using Coosu.Beatmap.Sections.GamePlay;
using KeyAsio.Common;
using KeyAsio.Core.Audio;
using KeyAsio.Core.OsuAudio.Hitsounds;
using KeyAsio.Core.OsuAudio.Hitsounds.Playback;
using KeyAsio.Sync.Events;
using Microsoft.Extensions.Logging;

namespace KeyAsio.Sync.Services;

public class GameplaySessionManager
{
    private readonly ILogger<GameplaySessionManager> _logger;
    private readonly GameplayAudioService _gameplayAudioService;
    private readonly IPlaybackEngine _audioEngine;
    private readonly SyncSessionContext _syncSessionContext;
    private readonly BeatmapHitsoundLoader _beatmapHitsoundLoader;
    private readonly SfxPlaybackService _sfxPlaybackService;

    public event SignalEventHandler? SessionStopped;

    private readonly Dictionary<GameMode, IHitsoundSequencer> _audioProviderDictionary = new();
    private readonly Lock _sessionLock = new();
    private long _sessionVersion;
    private volatile OsuFile? _osuFile;
    private volatile IHitsoundSequencer? _cachedHitsoundSequencer;
    private string? _lastCachedFolder;
    private GCLatencyMode _oldLatencyMode;
    private bool _isLowLatencyModeActive;
    private bool _hasActiveSession;

    public GameplaySessionManager(ILogger<GameplaySessionManager> logger,
        GameplayAudioService gameplayAudioService,
        IPlaybackEngine audioEngine,
        SyncSessionContext syncSessionContext,
        BeatmapHitsoundLoader beatmapHitsoundLoader,
        SfxPlaybackService sfxPlaybackService)
    {
        _logger = logger;
        _gameplayAudioService = gameplayAudioService;
        _audioEngine = audioEngine;
        _syncSessionContext = syncSessionContext;
        _beatmapHitsoundLoader = beatmapHitsoundLoader;
        _sfxPlaybackService = sfxPlaybackService;
    }

    public OsuFile? OsuFile
    {
        get => _osuFile;
        internal set
        {
            _osuFile = value;
            UpdateCachedSequencer();
        }
    }

    public string? AudioFilename { get; internal set; }
    public string? BeatmapFolder { get; private set; }
    public IReadOnlyList<PlaybackEvent> PlaybackList => _beatmapHitsoundLoader.PlaybackList;
    public List<SampleEvent> KeyList => _beatmapHitsoundLoader.KeyList;

    public void InitializeProviders(IHitsoundSequencer standardHitsoundSequencer,
        IHitsoundSequencer taikoHitsoundSequencer,
        IHitsoundSequencer catchHitsoundSequencer,
        IHitsoundSequencer maniaHitsoundSequencer)
    {
        _audioProviderDictionary[GameMode.Circle] = standardHitsoundSequencer;
        _audioProviderDictionary[GameMode.Taiko] = taikoHitsoundSequencer;
        _audioProviderDictionary[GameMode.Catch] = catchHitsoundSequencer;
        _audioProviderDictionary[GameMode.Mania] = maniaHitsoundSequencer;
        UpdateCachedSequencer();
    }

    public IHitsoundSequencer CurrentHitsoundSequencer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _cachedHitsoundSequencer ?? _audioProviderDictionary[GameMode.Circle];
    }

    private void UpdateCachedSequencer()
    {
        if (_audioProviderDictionary.Count == 0) return;

        _cachedHitsoundSequencer = GetSequencer(_osuFile?.General?.Mode ?? GameMode.Circle);
    }

    private IHitsoundSequencer GetSequencer(GameMode mode) =>
        _audioProviderDictionary.TryGetValue(mode, out var sequencer)
            ? sequencer
            : _audioProviderDictionary[GameMode.Circle];

    public async Task StartAsync(string? beatmapFilenameFull, string? beatmapFilename)
    {
        long version;
        lock (_sessionLock)
        {
            version = ++_sessionVersion;
            _syncSessionContext.IsStarted = false;
            OsuFile = null;
        }

        try
        {
            _logger.LogInformation("Start playing.");

            var resourceCatalog = _syncSessionContext.BeatmapResourceCatalog;
            var folder = ResolveBeatmapFolder(beatmapFilenameFull, beatmapFilename, resourceCatalog);
            if (folder == null)
            {
                throw new DirectoryNotFoundException("Failed to determine the beatmap directory.");
            }

            if (beatmapFilename == null)
            {
                throw new ArgumentNullException(nameof(beatmapFilename), "Beatmap filename cannot be null.");
            }

            var loaded = await _beatmapHitsoundLoader.LoadHitsoundNodesAsync(
                folder, beatmapFilename, _syncSessionContext.PlayMods, resourceCatalog);
            if (loaded is not { } hitsounds) return;

            var cacheContextKey = resourceCatalog != null
                ? $"{resourceCatalog.CacheKey}|{beatmapFilename}"
                : folder;

            lock (_sessionLock)
            {
                if (version != _sessionVersion) return;

                var sequencer = GetSequencer(hitsounds.Beatmap.General?.Mode ?? GameMode.Circle);
                _beatmapHitsoundLoader.SetNodeLists(sequencer, hitsounds.Events);
                OsuFile = hitsounds.Beatmap;
                AudioFilename = hitsounds.Beatmap.General?.AudioFilename;
                BeatmapFolder = folder;

                var playTime = _syncSessionContext.PlayTime;
                sequencer.SeekTo(playTime);
                PrepareAudioContext(folder, AudioFilename, resourceCatalog, cacheContextKey);

                if (!_isLowLatencyModeActive)
                {
                    _oldLatencyMode = GCSettings.LatencyMode;
                    GCSettings.LatencyMode = RuntimeInfo.IsSatori
                        ? GCLatencyMode.LowLatency
                        : GCLatencyMode.SustainedLowLatency;
                    _isLowLatencyModeActive = true;
                    _logger.LogInformation("GC LatencyMode set to {Mode}", GCSettings.LatencyMode);
                }

                _hasActiveSession = true;
                _syncSessionContext.IsStarted = true;
                // Prime hitsounds first so initial notes are cached immediately, then precache music/skin.
                _beatmapHitsoundLoader.PrimeCachingWindow(playTime);
                if (_audioEngine.CurrentDevice != null)
                {
                    _gameplayAudioService.PrecacheMusicAndSkinInBackground();
                }
            }
        }
        catch (Exception ex)
        {
            lock (_sessionLock)
            {
                if (version != _sessionVersion) return;
                _syncSessionContext.IsStarted = false;
            }

            _logger.LogError(ex, "Error while starting a beatmap. Filename: {BeatmapFilename}. FilenameReal: {OsuFile}",
                beatmapFilename, OsuFile);
        }
    }

    private static string? ResolveBeatmapFolder(string? beatmapFilenameFull, string? beatmapFilename,
        IBeatmapResourceCatalog? resourceCatalog)
    {
        if (beatmapFilename != null && resourceCatalog?.TryResolve(beatmapFilename, out var mappedBeatmap) == true)
        {
            return Path.GetDirectoryName(mappedBeatmap.Path);
        }

        return beatmapFilenameFull != null
            ? Path.GetDirectoryName(beatmapFilenameFull)
            : resourceCatalog?.RootPath;
    }

    private void PrepareAudioContext(string newFolder, string? audioFilename, IBeatmapResourceCatalog? resourceCatalog,
        string cacheContextKey)
    {
        if (_lastCachedFolder != null && _lastCachedFolder != cacheContextKey)
        {
            _logger.LogInformation("Cleaning caches caused by folder changing.");
            _gameplayAudioService.ClearCaches();
        }

        _lastCachedFolder = cacheContextKey;

        if (_audioEngine.CurrentDevice == null)
        {
            _logger.LogWarning($"AudioEngine is null, stop adding cache.");
            return;
        }

        _gameplayAudioService.SetContext(newFolder, audioFilename, resourceCatalog);
    }

    public void Stop()
    {
        long version;
        bool wasActive;
        lock (_sessionLock)
        {
            version = ++_sessionVersion;
            wasActive = _hasActiveSession || _syncSessionContext.IsStarted || _isLowLatencyModeActive;
            _hasActiveSession = false;
            _syncSessionContext.IsStarted = false;
            if (_isLowLatencyModeActive)
            {
                GCSettings.LatencyMode = _oldLatencyMode;
                _isLowLatencyModeActive = false;
                _logger.LogInformation("GC LatencyMode restored to {Mode}", _oldLatencyMode);
            }

            if (wasActive)
            {
                _logger.LogInformation("Stop playing.");
            }

            var mixer = _audioEngine.EffectMixer;
            _sfxPlaybackService.ClearAllLoops(mixer);
            mixer?.RemoveAllMixerInputs();
        }

        if (wasActive)
        {
            SessionStopped?.Invoke();
        }

        lock (_sessionLock)
        {
            if (version != _sessionVersion) return;
            _syncSessionContext.BaseMemoryTime = 0;
            _syncSessionContext.Combo = 0;
        }
    }

    internal void InvalidatePendingStart()
    {
        lock (_sessionLock)
        {
            _sessionVersion++;
        }
    }
}
