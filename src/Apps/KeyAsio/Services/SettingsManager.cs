using System.ComponentModel;
using Avalonia;
using Avalonia.Controls.Notifications;
using Avalonia.Styling;
using Avalonia.Threading;
using KeyAsio.Core.Audio;
using KeyAsio.Configuration;
using KeyAsio.Configuration.Models;
using KeyAsio.Application.Models;
using Microsoft.Extensions.Logging;
using Milki.Extensions.Configuration;
using SukiUI.Toasts;

namespace KeyAsio.Services;

public class SettingsManager : IDisposable
{
    private readonly ILogger<SettingsManager> _logger;
    private readonly AppSettings _appSettings;
    private readonly IPlaybackEngine _playbackEngine;
    private readonly ISukiToastManager _toastManager;

    private readonly List<INotifyPropertyChanged> _observedSettings = new();
    private readonly Lock _debounceLock = new();
    private readonly Lock _fileSaveLock = new();
    private CancellationTokenSource? _saveDebounceCts;
    private volatile bool _disposed;

    public SettingsManager(
        ILogger<SettingsManager> logger,
        AppSettings appSettings,
        IPlaybackEngine playbackEngine,
        ISukiToastManager toastManager)
    {
        _logger = logger;
        _appSettings = appSettings;
        _playbackEngine = playbackEngine;
        _toastManager = toastManager;

        ApplyTheme();
        SubscribeToSettingsChanges();
    }

    private void ApplyTheme()
    {
        if (Avalonia.Application.Current == null) return;

        Dispatcher.UIThread.Post(() =>
        {
            var theme = _appSettings.General.Theme;
            Avalonia.Application.Current.RequestedThemeVariant = theme switch
            {
                AppTheme.Light => ThemeVariant.Light,
                AppTheme.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default
            };
        });
    }

    private void SubscribeToSettingsChanges()
    {
        void Subscribe(INotifyPropertyChanged? obj)
        {
            if (obj != null)
            {
                obj.PropertyChanged += OnSettingsChanged;
                _observedSettings.Add(obj);
            }
        }

        Subscribe(_appSettings.General);
        Subscribe(_appSettings.Input);
        Subscribe(_appSettings.Paths);
        Subscribe(_appSettings.Audio);
        Subscribe(_appSettings.Logging);
        Subscribe(_appSettings.Performance);
        Subscribe(_appSettings.Sync);
        Subscribe(_appSettings.Sync.Scanning);
        Subscribe(_appSettings.Sync.Playback);
        Subscribe(_appSettings.Sync.Filters);
        Subscribe(_appSettings.Update);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is AppSettingsAudio)
        {
            if (e.PropertyName == nameof(AppSettingsAudio.MasterVolume))
            {
                _playbackEngine.MainVolume = _appSettings.Audio.MasterVolume / 100f;
                DebounceSave();
            }
            else if (e.PropertyName == nameof(AppSettingsAudio.MusicVolume))
            {
                _playbackEngine.MusicVolume = _appSettings.Audio.MusicVolume / 100f;
                DebounceSave();
            }
            else if (e.PropertyName == nameof(AppSettingsAudio.EffectVolume))
            {
                _playbackEngine.EffectVolume = _appSettings.Audio.EffectVolume / 100f;
                DebounceSave();
            }
            else if (e.PropertyName == nameof(AppSettingsAudio.EnableExtendedVolume))
            {
                SaveSettingsImmediate();
            }
        }
        else if (sender is AppSettingsSyncPlayback &&
                 e.PropertyName is nameof(AppSettingsSyncPlayback.BalanceFactor)
                     or nameof(AppSettingsSyncPlayback.BalanceMode)
                     or nameof(AppSettingsSyncPlayback.LimiterType))
        {
            if (e.PropertyName == nameof(AppSettingsSyncPlayback.LimiterType))
            {
                _playbackEngine.LimiterType = _appSettings.Sync.Playback.LimiterType;
            }

            DebounceSave();
        }
        else
        {
            if (sender is AppSettingsGeneral && e.PropertyName == nameof(AppSettingsGeneral.Theme))
            {
                ApplyTheme();
            }

            SaveSettingsImmediate();
        }
    }

    private void SaveSettingsImmediate()
    {
        if (_disposed) return;

        try
        {
            lock (_fileSaveLock)
            {
                if (_disposed) return;
                _appSettings.Save();
            }
        }
        catch (Exception ex)
        {
            HandleSaveException(ex);
        }
    }

    private void HandleSaveException(Exception ex)
    {
        _logger?.LogError(ex, "Failed to save settings");
        Dispatcher.UIThread.Post(() =>
        {
            _toastManager.CreateToast()
                .OfType(NotificationType.Error)
                .WithTitle("Settings Save Failed")
                .WithContent($"Could not save configuration: {ex.Message}")
                .Queue();
        });
    }

    private void DebounceSave()
    {
        CancellationToken token;
        lock (_debounceLock)
        {
            if (_disposed) return;
            var previousCts = _saveDebounceCts;
            _saveDebounceCts = new CancellationTokenSource();
            token = _saveDebounceCts.Token;

            if (previousCts != null)
            {
                previousCts.Cancel();
                previousCts.Dispose();
            }
        }

        _ = Task.Delay(500, token).ContinueWith(t =>
        {
            if (t.IsCanceled || _disposed) return;
            Dispatcher.UIThread.Post(SaveSettingsImmediate);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    public void Dispose()
    {
        CancellationTokenSource? ctsToDispose;
        lock (_debounceLock)
        {
            if (_disposed) return;
            _disposed = true;
            ctsToDispose = _saveDebounceCts;
            _saveDebounceCts = null;
        }

        if (ctsToDispose != null)
        {
            ctsToDispose.Cancel();
            ctsToDispose.Dispose();
        }

        foreach (var obj in _observedSettings)
        {
            obj.PropertyChanged -= OnSettingsChanged;
        }

        _observedSettings.Clear();
        GC.SuppressFinalize(this);
    }
}
