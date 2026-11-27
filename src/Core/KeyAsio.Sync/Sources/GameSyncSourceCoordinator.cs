using KeyAsio.Configuration.Models;
using Microsoft.Extensions.Logging;

namespace KeyAsio.Sync.Sources;

public sealed class GameSyncSourceCoordinator
{
    private readonly SyncSessionContext _syncSessionContext;
    private readonly IGameSyncSource[] _sources;
    private readonly ILogger<GameSyncSourceCoordinator> _logger;
    private readonly Lock _syncLock = new();
    private IGameSyncSource? _activeSource;
    private bool _started;
    private GameSyncSnapshot? _disconnectedSnapshot;
    private GameClientType _lastClientType = GameClientType.Stable;

    public event Action<GameClientType>? ClientTypeChanged;

    public GameSyncSourceCoordinator(
        SyncSessionContext syncSessionContext,
        IEnumerable<IGameSyncSource> sources,
        ILogger<GameSyncSourceCoordinator> logger)
    {
        _syncSessionContext = syncSessionContext;
        _sources = sources.OrderByDescending(source => source.Priority).ToArray();
        _logger = logger;

        foreach (var source in _sources)
        {
            source.AvailabilityChanged += OnSourceAvailabilityChanged;
            source.SnapshotReceived += OnSourceSnapshotReceived;
        }
    }

    public void Start()
    {
        lock (_syncLock)
        {
            if (_started) return;
            _started = true;
        }

        foreach (var source in _sources)
        {
            source.Start();
        }

        GameClientType? changedClientType;
        lock (_syncLock)
        {
            if (!_started) return;
            changedClientType = SelectActiveSourceLocked(applyCurrentSnapshot: true);
        }

        NotifyClientTypeChanged(changedClientType);
    }

    public async Task StopAsync()
    {
        lock (_syncLock)
        {
            if (!_started) return;
            _started = false;
        }

        foreach (var source in _sources)
        {
            await source.StopAsync().ConfigureAwait(false);
        }

        GameClientType? changedClientType;
        lock (_syncLock)
        {
            if (_started) return;
            _activeSource = null;
            changedClientType = ApplyDisconnectedSnapshotLocked(GameClientType.Stable);
        }

        NotifyClientTypeChanged(changedClientType);
    }

    private void OnSourceAvailabilityChanged(IGameSyncSource source, bool isAvailable)
    {
        GameClientType? changedClientType;
        lock (_syncLock)
        {
            if (!_started) return;

            if (!isAvailable && ReferenceEquals(_activeSource, source))
            {
                _logger.LogInformation("Game sync source unavailable: {Source}", source.Name);
            }
            else if (isAvailable)
            {
                _logger.LogInformation("Game sync source available: {Source}", source.Name);
            }

            changedClientType = SelectActiveSourceLocked(applyCurrentSnapshot: true);
        }

        NotifyClientTypeChanged(changedClientType);
    }

    private void OnSourceSnapshotReceived(IGameSyncSource source, GameSyncSnapshot snapshot)
    {
        GameClientType? changedClientType = null;
        lock (_syncLock)
        {
            if (!_started) return;

            var selectionChanged = SelectActiveSourceLocked(applyCurrentSnapshot: false);
            if (!ReferenceEquals(_activeSource, source))
            {
                changedClientType = selectionChanged;
            }
            else
            {
                changedClientType = ApplySnapshotLocked(snapshot) ?? selectionChanged;
            }
        }

        NotifyClientTypeChanged(changedClientType);
    }

    private GameClientType? SelectActiveSourceLocked(bool applyCurrentSnapshot)
    {
        var nextSource = _sources.FirstOrDefault(source => source.IsAvailable);
        if (ReferenceEquals(_activeSource, nextSource))
        {
            return null;
        }

        var oldSource = _activeSource;
        _activeSource = nextSource;

        _logger.LogInformation("Game sync source switched: {OldSource} -> {NewSource}",
            oldSource?.Name ?? "none", nextSource?.Name ?? "none");

        if (nextSource != null)
        {
            if (applyCurrentSnapshot)
            {
                return ApplySnapshotLocked(nextSource.CurrentSnapshot);
            }

            return null;
        }

        return ApplyDisconnectedSnapshotLocked(oldSource?.ClientType ?? GameClientType.Stable);
    }

    private GameClientType? ApplySnapshotLocked(GameSyncSnapshot snapshot)
    {
        GameClientType? changedClientType = null;
        try
        {
            _syncSessionContext.ClientType = snapshot.ClientType;
            if (snapshot.ClientType != _lastClientType)
            {
                _lastClientType = snapshot.ClientType;
                changedClientType = snapshot.ClientType;
            }

            _syncSessionContext.ProcessId = snapshot.ProcessId;
            _syncSessionContext.Username = snapshot.Username;
            _syncSessionContext.PlayMods = snapshot.PlayMods;
            _syncSessionContext.IsReplay = snapshot.IsReplay;
            _syncSessionContext.Score = snapshot.Score;
            _syncSessionContext.Statistics = snapshot.Statistics;
            _syncSessionContext.HitErrors = snapshot.HitErrors;
            _syncSessionContext.BeatmapResourceCatalog = snapshot.BeatmapResourceCatalog;
            _syncSessionContext.Beatmap = snapshot.Beatmap;
            _syncSessionContext.BeatmapOffset = snapshot.BeatmapOffset;
            _syncSessionContext.BaseMemoryTime = snapshot.PlayTime;
            _syncSessionContext.OsuStatus = snapshot.Status;
            _syncSessionContext.Combo = snapshot.Combo;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to apply game sync snapshot from {ClientType}.", snapshot.ClientType);
        }

        return changedClientType;
    }

    private GameClientType? ApplyDisconnectedSnapshotLocked(GameClientType disconnectedClientType)
    {
        var fallbackClientType = disconnectedClientType == GameClientType.Lazer
            ? GameClientType.Stable
            : disconnectedClientType;

        // Reuse a single disconnected snapshot under _syncLock to avoid allocating on the
        // low-frequency disconnect path.
        _disconnectedSnapshot ??= GameSyncSnapshot.NotRunning(fallbackClientType);
        _disconnectedSnapshot.ResetToNotRunning(fallbackClientType);
        return ApplySnapshotLocked(_disconnectedSnapshot);
    }

    private void NotifyClientTypeChanged(GameClientType? clientType)
    {
        if (clientType.HasValue)
        {
            ClientTypeChanged?.Invoke(clientType.Value);
        }
    }
}
