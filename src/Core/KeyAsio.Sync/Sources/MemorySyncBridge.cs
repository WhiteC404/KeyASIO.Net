using System.ComponentModel;
using System.Text;
using KeyAsio.Common;
using KeyAsio.Configuration;
using Microsoft.Extensions.Logging;

namespace KeyAsio.Sync.Sources;

public class MemorySyncBridge : IDisposable
{
    private readonly GameSyncSourceCoordinator _sourceCoordinator;
    private readonly StableMemoryGameSyncSource _stableSource;
    private readonly LazerIpcGameSyncSource _lazerSource;
    private readonly SyncSessionContext _syncSessionContext;
    private readonly AppSettings _appSettings;
    private readonly ILogger<MemorySyncBridge> _logger;
    private readonly SemaphoreSlim _scanTransition = new(1, 1);
    private bool _initialized;
    private bool _isRunning;
    private volatile bool _isStopping;
    private volatile bool _disposed;

    public MemorySyncBridge(
        GameSyncSourceCoordinator sourceCoordinator,
        StableMemoryGameSyncSource stableSource,
        LazerIpcGameSyncSource lazerSource,
        SyncSessionContext syncSessionContext,
        AppSettings appSettings,
        ILogger<MemorySyncBridge> logger)
    {
        _sourceCoordinator = sourceCoordinator;
        _stableSource = stableSource;
        _lazerSource = lazerSource;
        _syncSessionContext = syncSessionContext;
        _appSettings = appSettings;
        _logger = logger;
    }

    public void Start()
    {
        if (_initialized) return;
        _initialized = true;

        _appSettings.Sync.Scanning.PropertyChanged += OnScanningSettingsChanged;
        _appSettings.Sync.PropertyChanged += OnSyncSettingsChanged;
        _lazerSource.AvailabilityChanged += OnLazerAvailabilityChanged;

        ConfigureStableSourceIntervals();

        _logger.LogInformation("Initial EnableSync state: {State}", _appSettings.Sync.EnableSync);
        _logger.LogInformation("Initial EnableMixSync state: {State}", _appSettings.Sync.EnableMixSync);

        _ = ApplySyncSettingAsync();
    }

    private void OnScanningSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettingsSyncScanning.GeneralScanInterval)
            or nameof(AppSettingsSyncScanning.TimingScanInterval))
        {
            ConfigureStableSourceIntervals();
        }
    }

    private void ConfigureStableSourceIntervals()
    {
        _stableSource.ConfigureIntervals(_appSettings.Sync.Scanning.GeneralScanInterval,
            _appSettings.Sync.Scanning.TimingScanInterval);
    }

    private void OnLazerAvailabilityChanged(IGameSyncSource source, bool _)
    {
        if (_isStopping) return;

        _stableSource.SetMemoryScanSuppressed(source.IsAvailable);
    }

    private void OnSyncSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettingsSync.EnableSync))
        {
            _ = ApplySyncSettingAsync();
        }
        else if (e.PropertyName == nameof(AppSettingsSync.EnableMixSync))
        {
            _logger.LogInformation("EnableMixSync changed to: {State}", _appSettings.Sync.EnableMixSync);
        }
    }

    private async Task ApplySyncSettingAsync()
    {
        await _scanTransition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;

            var enableSync = _appSettings.Sync.EnableSync;
            if (enableSync == _isRunning) return;

            _logger.LogInformation("Changing memory sync state to {Enabled}.", enableSync);
            if (enableSync)
                await StartScanningAsync().ConfigureAwait(false);
            else
                await StopScanningAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to change the memory sync state.");
        }
        finally
        {
            _scanTransition.Release();
        }
    }

    private async Task StartScanningAsync()
    {
        try
        {
            var player = EncodeUtils.FromBase64String(_appSettings.Logging.PlayerBase64 ?? "", Encoding.ASCII);
            _syncSessionContext.Username = player;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to decode PlayerBase64 string.");
        }

        ConfigureStableSourceIntervals();
        try
        {
            _sourceCoordinator.Start();
            _isRunning = true;
        }
        catch
        {
            // Roll back partially started sources so _isRunning stays consistent with coordinator state.
            try
            {
                await StopScanningAsync().ConfigureAwait(false);
            }
            catch (Exception rollbackEx)
            {
                _logger.LogWarning(rollbackEx, "Failed to roll back sync sources after a partial start failure.");
            }

            throw;
        }
    }

    private async Task StopScanningAsync()
    {
        _isStopping = true;
        try
        {
            await _sourceCoordinator.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _stableSource.SetMemoryScanSuppressed(false);
            _isStopping = false;
            _isRunning = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_initialized)
        {
            _appSettings.Sync.Scanning.PropertyChanged -= OnScanningSettingsChanged;
            _appSettings.Sync.PropertyChanged -= OnSyncSettingsChanged;
            _lazerSource.AvailabilityChanged -= OnLazerAvailabilityChanged;
        }

        _scanTransition.Wait();
        try
        {
            if (_isRunning)
            {
                StopScanningAsync().GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop memory sync bridge during disposal.");
        }
        finally
        {
            _scanTransition.Release();
        }
    }
}
