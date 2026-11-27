using Coosu.Beatmap;
using KeyAsio.Common;
using KeyAsio.Configuration;
using KeyAsio.Core.OsuAudio.Hitsounds;
using KeyAsio.Core.OsuAudio.Hitsounds.Playback;
using KeyAsio.Plugins.Contracts.Sync;
using Microsoft.Extensions.Logging;

namespace KeyAsio.Sync.Services;

public class BeatmapHitsoundLoader
{
    private readonly ILogger<BeatmapHitsoundLoader> _logger;
    private readonly AppSettings _appSettings;
    private readonly GameplayAudioService _gameplayAudioService;

    private List<SampleEvent> _keyList = new();
    private List<PlaybackEvent> _playbackList = new();
    private int _nextCachingTime;

    public BeatmapHitsoundLoader(ILogger<BeatmapHitsoundLoader> logger, AppSettings appSettings,
        GameplayAudioService gameplayAudioService)
    {
        _logger = logger;
        _appSettings = appSettings;
        _gameplayAudioService = gameplayAudioService;
    }

    public IReadOnlyList<PlaybackEvent> PlaybackList => _playbackList;
    public List<SampleEvent> KeyList => _keyList;

    internal async Task<(OsuFile Beatmap, List<PlaybackEvent> Events)?> LoadHitsoundNodesAsync(
        string folder, string diffFilename, Mods playMods, IBeatmapResourceCatalog? resourceCatalog)
    {
        var beatmapSetContext = resourceCatalog != null
            ? new BeatmapSetContext(resourceCatalog)
            : new BeatmapSetContext(folder);
        using (DebugUtils.CreateTimer("InitFolder", _logger))
        {
            await beatmapSetContext.InitializeAsync(diffFilename,
                ignoreWaveFiles: _appSettings.Sync.Filters.DisableBeatmapHitsounds);
        }

        if (beatmapSetContext.OsuFiles.Count <= 0)
        {
            _logger.LogWarning("There is no available beatmaps after scanning. Directory: {Folder}; File: {Filename}",
                folder, diffFilename);
            return null;
        }

        var osuFile = beatmapSetContext.OsuFiles[0];

        using var _ = DebugUtils.CreateTimer("InitAudio", _logger);
        var hitsoundList = await beatmapSetContext.GetHitsoundNodesAsync(osuFile);
        await Task.Delay(100);

        var isNightcore = playMods != Mods.Unknown && (playMods & Mods.Nightcore) != 0;
        if (isNightcore || _appSettings.Sync.Playback.NightcoreBeats)
        {
            if (isNightcore)
            {
                _logger.LogInformation("Current Mods: {PlayMods}", playMods);
            }

            var list = NightcoreBeatGenerator.GetHitsoundNodes(osuFile, TimeSpan.Zero);
            hitsoundList.AddRange(list);
            hitsoundList = hitsoundList.OrderBy(k => k.Offset).ToList();
        }

        return (osuFile, hitsoundList);
    }

    internal void SetNodeLists(IHitsoundSequencer hitsoundSequencer, IReadOnlyList<PlaybackEvent> events)
    {
        var keyList = new List<SampleEvent>();
        var playbackList = new List<PlaybackEvent>();
        hitsoundSequencer.FillAudioList(events, keyList, playbackList);
        _keyList = keyList;
        _playbackList = playbackList;
    }

    public void CacheAllHitsounds()
    {
        _gameplayAudioService.PrecacheHitsoundsRangeInBackground(0, int.MaxValue, _keyList);
        _gameplayAudioService.PrecacheHitsoundsRangeInBackground(0, int.MaxValue, _playbackList);
    }

    public void ResetNodes(IHitsoundSequencer hitsoundSequencer, int playTime)
    {
        hitsoundSequencer.SeekTo(playTime); // slow
        PrimeCachingWindow(playTime);
    }

    internal void PrimeCachingWindow(int playTime)
    {
        var startTime = Math.Max(0, playTime - 200);
        _nextCachingTime = startTime + 10000;
        _gameplayAudioService.PrecacheHitsoundsRangeInBackground(startTime, startTime + 13000, _keyList);
        _gameplayAudioService.PrecacheHitsoundsRangeInBackground(startTime, startTime + 13000, _playbackList);
    }

    public void AdvanceCachingWindow(int newMs)
    {
        if (newMs > _nextCachingTime)
        {
            AddAudioCacheInBackground(_nextCachingTime, _nextCachingTime + 13000, _keyList);
            AddAudioCacheInBackground(_nextCachingTime, _nextCachingTime + 13000, _playbackList);
            _nextCachingTime += 10000;
        }
    }

    private void AddAudioCacheInBackground(int startTime, int endTime, IEnumerable<PlaybackEvent> playableNodes)
    {
        _gameplayAudioService.PrecacheHitsoundsRangeInBackground(startTime, endTime, playableNodes);
    }
}
