using KeyAsio.Configuration;
using KeyAsio.Configuration.Models;
using KeyAsio.Configuration.Serialization;
using KeyAsio.Core.Audio;
using KeyAsio.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Milki.Extensions.Configuration;
using Moq;
using SukiUI.Toasts;

namespace KeyAsio.UnitTests;

public sealed class SettingsManagerTests : IDisposable
{
    private const string Filename = "appsettings.yaml";
    private readonly string _folder = Directory.CreateTempSubdirectory("keyasio-settings-").FullName;
    private readonly MyYamlConfigurationConverter _converter = MyYamlConfigurationConverter.Instance;

    [Fact]
    public void ChangingNightcoreBeats_SavesTheSetting()
    {
        var settings = ConfigurationFactory.GetConfiguration<AppSettings>(_folder, Filename, _converter);
        using var manager = CreateManager(settings);

        settings.Sync.Playback.NightcoreBeats = true;

        Assert.True(ReadSavedSettings().Sync.Playback.NightcoreBeats);
    }

    [Fact]
    public void ChangingSliderTailBehavior_SavesTheSetting()
    {
        var settings = ConfigurationFactory.GetConfiguration<AppSettings>(_folder, Filename, _converter);
        using var manager = CreateManager(settings);

        settings.Sync.Playback.TailPlaybackBehavior = SliderTailPlaybackBehavior.KeepReverse;

        Assert.Equal(SliderTailPlaybackBehavior.KeepReverse, ReadSavedSettings().Sync.Playback.TailPlaybackBehavior);
    }

    [Fact]
    public void ChangingEnableExtendedVolume_SavesTheSetting()
    {
        var settings = ConfigurationFactory.GetConfiguration<AppSettings>(_folder, Filename, _converter);
        using var manager = CreateManager(settings);

        settings.Audio.EnableExtendedVolume = true;

        Assert.True(ReadSavedSettings().Audio.EnableExtendedVolume);
    }

    [Fact]
    public void ChangingSampleRate_DoesNotBypassAudioDeviceOperationCoordinatorPersistence()
    {
        var settings = ConfigurationFactory.GetConfiguration<AppSettings>(_folder, Filename, _converter);
        using var manager = CreateManager(settings);
        var initialSampleRate = ReadSavedSettings().Audio.SampleRate;

        settings.Audio.SampleRate = initialSampleRate == 48000 ? 96000 : 48000;

        Assert.Equal(initialSampleRate, ReadSavedSettings().Audio.SampleRate);
    }

    private static SettingsManager CreateManager(AppSettings settings) =>
        new(NullLogger<SettingsManager>.Instance, settings, Mock.Of<IPlaybackEngine>(), Mock.Of<ISukiToastManager>());

    private AppSettings ReadSavedSettings() => Assert.IsType<AppSettings>(
        _converter.DeserializeSettings(File.ReadAllText(Path.Combine(_folder, Filename)), typeof(AppSettings)));

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
