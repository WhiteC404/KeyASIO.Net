using Coosu.Beatmap.Sections.GamePlay;
using KeyAsio.Configuration;
using KeyAsio.Core.Audio;
using KeyAsio.Core.Audio.Caching;
using KeyAsio.Core.OsuAudio.Hitsounds;
using KeyAsio.Core.OsuAudio.Hitsounds.Playback;
using KeyAsio.Plugins.Contracts.Sync;
using KeyAsio.Sync;
using KeyAsio.Sync.Abstractions;
using KeyAsio.Sync.AudioProviders;
using KeyAsio.Sync.Models;
using KeyAsio.Sync.Services;
using KeyAsio.Sync.Sources;
using KeyAsio.Sync.States;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NAudio.Wave;

namespace KeyAsio.UnitTests;

public sealed class GameplaySessionManagerTests : IDisposable
{
    private const string BeatmapFilename = "test.osu";
    private readonly string _folder = Directory.CreateTempSubdirectory("keyasio-beatmap-").FullName;
    private readonly AppSettings _settings = new();
    private readonly IPlaybackEngine _engine = Mock.Of<IPlaybackEngine>();
    private readonly IPlaybackRuntimeState _runtime = Mock.Of<IPlaybackRuntimeState>();
    private readonly Mock<IHitsoundSequencer> _standard = new();
    private readonly Mock<IHitsoundSequencer> _mania = new();
    private readonly SyncSessionContext _context;
    private readonly GameplayAudioService _audio;
    private readonly BeatmapHitsoundLoader _loader;
    private readonly SfxPlaybackService _sfx;
    private readonly GameplaySessionManager _manager;

    public GameplaySessionManagerTests()
    {
        _context = new SyncSessionContext(_settings);
        _audio = new GameplayAudioService(NullLogger<GameplayAudioService>.Instance, _context, _settings,
            _engine, new AudioCacheManager(NullLogger<AudioCacheManager>.Instance), _runtime,
            Mock.Of<ISkinResourceProvider>());
        _loader = new BeatmapHitsoundLoader(NullLogger<BeatmapHitsoundLoader>.Instance, _settings, _audio);
        _sfx = new SfxPlaybackService(NullLogger<SfxPlaybackService>.Instance, _engine, _settings);
        _manager = new GameplaySessionManager(NullLogger<GameplaySessionManager>.Instance, _audio, _engine,
            _context, _loader, _sfx);
        _manager.InitializeProviders(_standard.Object, Mock.Of<IHitsoundSequencer>(),
            Mock.Of<IHitsoundSequencer>(), _mania.Object);
    }

    [Fact]
    public async Task StartAsync_SeeksBeforePublishingTheSession()
    {
        var startedDuringSeek = true;
        _standard.Setup(sequencer => sequencer.SeekTo(It.IsAny<int>()))
            .Callback(() => startedDuringSeek = _context.IsStarted);

        await _manager.StartAsync(WriteBeatmap(GameMode.Circle), BeatmapFilename);

        Assert.True(_context.IsStarted);
        Assert.False(startedDuringSeek);
        _standard.Verify(sequencer => sequencer.SeekTo(It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public async Task FirstStart_PrecachesTheNoteForKeyboardPlayback()
    {
        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        Mock.Get(_engine).SetupGet(value => value.CurrentDevice).Returns(Mock.Of<IWavePlayer>());
        Mock.Get(_engine).SetupGet(value => value.EngineWaveFormat).Returns(waveFormat);
        Mock.Get(_engine).SetupGet(value => value.SourceWaveFormat).Returns(waveFormat);
        Mock.Get(_runtime).SetupGet(state => state.SelectedSkinFolder).Returns("{internal}");
        var sequencer = new StandardHitsoundSequencer(NullLogger<StandardHitsoundSequencer>.Instance,
            _settings, _context, _engine, _audio, _manager);
        _manager.InitializeProviders(sequencer, Mock.Of<IHitsoundSequencer>(),
            Mock.Of<IHitsoundSequencer>(), _mania.Object);
        _context.BaseMemoryTime = 500;

        await _manager.StartAsync(WriteBeatmap(GameMode.Circle), BeatmapFilename);

        var note = Assert.Single(_manager.KeyList);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!_audio.TryGetAudioByNode(note, out _))
        {
            await Task.Delay(10, timeout.Token);
        }

        var playback = new List<PlaybackInfo>();
        sequencer.ProcessInteraction(playback, 0, 2);

        Assert.Same(note, Assert.Single(playback).PlaybackEvent);
    }

    [Fact]
    public async Task StartAsync_UsesManiaSequencerToClassifyManiaEvents()
    {
        await _manager.StartAsync(WriteBeatmap(GameMode.Mania), BeatmapFilename);

        Assert.True(_context.IsStarted);
        Assert.Same(_mania.Object, _manager.CurrentHitsoundSequencer);
        _mania.Verify(sequencer => sequencer.FillAudioList(
            It.Is<IReadOnlyList<PlaybackEvent>>(events => events.Count > 0),
            It.IsAny<List<SampleEvent>>(), It.IsAny<List<PlaybackEvent>>()), Times.Once);
        _standard.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(OsuMemoryStatus.SongSelection)]
    [InlineData(OsuMemoryStatus.ResultsScreen)]
    [InlineData(OsuMemoryStatus.NotRunning)]
    public async Task LeavingGameplay_DiscardsAnInFlightBeatmapLoad(OsuMemoryStatus nextState)
    {
        var beatmapPath = WriteBeatmap(GameMode.Circle);
        _context.Beatmap = new BeatmapIdentifier(beatmapPath);
        var stateMachine = new GameStateMachine(new Dictionary<OsuMemoryStatus, IGameState>
        {
            [OsuMemoryStatus.Playing] = new PlayingState(NullLogger<PlayingState>.Instance, _settings, _engine,
                _loader, _sfx, _runtime, _manager, _audio),
            [OsuMemoryStatus.SongSelection] = new BrowsingState(_manager),
            [OsuMemoryStatus.ResultsScreen] = new ResultsState(),
            [OsuMemoryStatus.NotRunning] = new NotRunningState()
        });
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var allowRead = new ManualResetEventSlim();
        var catalog = new Mock<IBeatmapResourceCatalog>();
        var cancellationToken = TestContext.Current.CancellationToken;
        catalog.SetupGet(value => value.Resources).Returns(() =>
        {
            readStarted.SetResult();
            if (!allowRead.Wait(TimeSpan.FromSeconds(5), cancellationToken))
                throw new TimeoutException("The test did not release the beatmap load.");

            return [new BeatmapResource(BeatmapFilename, beatmapPath)];
        });
        _context.BeatmapResourceCatalog = catalog.Object;
        var loading = Task.Run(() => stateMachine.TransitionToAsync(_context, OsuMemoryStatus.Playing),
            cancellationToken);

        try
        {
            await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await stateMachine.TransitionToAsync(_context, nextState);
        }
        finally
        {
            allowRead.Set();
            await loading;
        }

        Assert.Equal(nextState, stateMachine.CurrentStatus);
        Assert.False(_context.IsStarted);
        Assert.Null(_manager.OsuFile);
        _standard.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(OsuMemoryStatus.SongSelection)]
    [InlineData(OsuMemoryStatus.ResultsScreen)]
    [InlineData(OsuMemoryStatus.NotRunning)]
    public async Task LeavingActiveGameplay_StopsSessionOnce(OsuMemoryStatus nextState)
    {
        var beatmapPath = WriteBeatmap(GameMode.Circle);
        _context.Beatmap = new BeatmapIdentifier(beatmapPath);
        var stateMachine = new GameStateMachine(new Dictionary<OsuMemoryStatus, IGameState>
        {
            [OsuMemoryStatus.Playing] = new PlayingState(NullLogger<PlayingState>.Instance, _settings, _engine,
                _loader, _sfx, _runtime, _manager, _audio),
            [OsuMemoryStatus.SongSelection] = new BrowsingState(_manager),
            [OsuMemoryStatus.ResultsScreen] = new ResultsState(),
            [OsuMemoryStatus.NotRunning] = new NotRunningState()
        });
        var stopCount = 0;
        _manager.SessionStopped += () => stopCount++;

        await stateMachine.TransitionToAsync(_context, OsuMemoryStatus.Playing);
        Assert.True(_context.IsStarted);

        await stateMachine.TransitionToAsync(_context, nextState);

        Assert.Equal(nextState, stateMachine.CurrentStatus);
        Assert.False(_context.IsStarted);
        Assert.Equal(1, stopCount);
    }

    [Fact]
    public async Task Mania4K_NoteAtMaxX512_SeeksWithoutOutOfRangeAndPlaysLastColumn()
    {
        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        Mock.Get(_engine).SetupGet(value => value.CurrentDevice).Returns(Mock.Of<IWavePlayer>());
        Mock.Get(_engine).SetupGet(value => value.EngineWaveFormat).Returns(waveFormat);
        Mock.Get(_engine).SetupGet(value => value.SourceWaveFormat).Returns(waveFormat);
        Mock.Get(_runtime).SetupGet(state => state.SelectedSkinFolder).Returns("{internal}");
        var maniaSequencer = new ManiaHitsoundSequencer(NullLogger<ManiaHitsoundSequencer>.Instance,
            _settings, _context, _runtime, _engine, _audio, _manager);
        _manager.InitializeProviders(_standard.Object, Mock.Of<IHitsoundSequencer>(),
            Mock.Of<IHitsoundSequencer>(), maniaSequencer);
        _context.BaseMemoryTime = 500;

        await _manager.StartAsync(WriteBeatmap(GameMode.Mania, hitObjectX: 512), BeatmapFilename);

        Assert.True(_context.IsStarted);
        var note = Assert.Single(_manager.KeyList);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!_audio.TryGetAudioByNode(note, out _))
        {
            await Task.Delay(10, timeout.Token);
        }

        var playback = new List<PlaybackInfo>();
        maniaSequencer.ProcessInteraction(playback, keyIndex: 3, keyTotal: 4);

        Assert.Same(note, Assert.Single(playback).PlaybackEvent);
    }

    [Fact]
    public async Task StandardSequencer_IgnoresManualInteractionDuringAutoplay()
    {
        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        Mock.Get(_engine).SetupGet(value => value.CurrentDevice).Returns(Mock.Of<IWavePlayer>());
        Mock.Get(_engine).SetupGet(value => value.EngineWaveFormat).Returns(waveFormat);
        Mock.Get(_engine).SetupGet(value => value.SourceWaveFormat).Returns(waveFormat);
        Mock.Get(_runtime).SetupGet(state => state.SelectedSkinFolder).Returns("{internal}");
        var sequencer = new StandardHitsoundSequencer(NullLogger<StandardHitsoundSequencer>.Instance,
            _settings, _context, _engine, _audio, _manager, _runtime);
        _manager.InitializeProviders(sequencer, Mock.Of<IHitsoundSequencer>(),
            Mock.Of<IHitsoundSequencer>(), _mania.Object);
        _context.PlayMods = Mods.Autoplay;
        _context.BaseMemoryTime = 500;

        await _manager.StartAsync(WriteBeatmap(GameMode.Circle), BeatmapFilename);

        var playback = new List<PlaybackInfo>();
        sequencer.ProcessInteraction(playback, 0, 2);

        Assert.Empty(playback);
    }

    private string WriteBeatmap(GameMode mode, int hitObjectX = 64)
    {
        var path = Path.Combine(_folder, BeatmapFilename);
        File.WriteAllText(path, $$"""
            osu file format v14

            [General]
            AudioFilename: test.wav
            Mode: {{(int)mode}}

            [Metadata]
            Title:Test
            Artist:Test
            Creator:Test
            Version:Test

            [Difficulty]
            HPDrainRate:5
            CircleSize:4
            OverallDifficulty:5
            ApproachRate:5
            SliderMultiplier:1.4
            SliderTickRate:1

            [TimingPoints]
            0,500,4,1,0,100,1,0

            [HitObjects]
            {{hitObjectX}},192,500,1,0,0:0:0:0:
            """);
        return path;
    }

    public void Dispose()
    {
        _manager.Stop();
        _audio.Dispose();
        Directory.Delete(_folder, recursive: true);
    }
}
