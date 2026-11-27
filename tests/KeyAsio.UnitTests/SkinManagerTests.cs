using System.Resources;
using System.Text;
using dnlib.DotNet;
using KeyAsio.Application.Abstractions;
using KeyAsio.Application.Models;
using KeyAsio.Application.Services;
using KeyAsio.Configuration;
using KeyAsio.Core.Audio.Caching;
using KeyAsio.Sync;
using KeyAsio.Sync.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace KeyAsio.UnitTests;

public sealed class SkinManagerTests : IDisposable
{
    private readonly string _temporaryFolder = Directory.CreateTempSubdirectory().FullName;

    [Fact]
    public async Task AllowAutoLoadSkins_DisabledThenReenabled_ReextractsStableDefaultComboBreak()
    {
        var osuFolder = Directory.CreateDirectory(Path.Combine(_temporaryFolder, "osu-stable")).FullName;
        var expectedComboBreak = CreatePcmWave([0x10, 0x20, 0x30, 0x40]);
        CreateSyntheticOsuGameplayDll(osuFolder, new Dictionary<string, byte[]>
        {
            ["combobreak"] = expectedComboBreak
        });

        var appSettings = new AppSettings();
        appSettings.Paths.OsuFolderPath = osuFolder;
        appSettings.Paths.AllowAutoLoadSkins = true;

        using var manager = CreateSkinManager(appSettings);

        await manager.ReloadSkinsAsync();
        await WaitUntilAsync(() => manager.TryGetStableResource("combobreak", out _));

        Assert.True(manager.TryGetStableResource("combobreak", out var initialBytes));
        Assert.Equal(expectedComboBreak, initialBytes);

        // Disabling AllowAutoLoadSkins clears extracted resources instead of poisoning them with empty arrays.
        appSettings.Paths.AllowAutoLoadSkins = false;
        await WaitUntilAsync(() => !manager.TryGetStableResource("combobreak", out _));
        Assert.False(manager.TryGetStableResource("combobreak", out _));

        // Re-enabling AllowAutoLoadSkins must re-extract osu!gameplay.dll resources cleanly.
        appSettings.Paths.AllowAutoLoadSkins = true;
        await WaitUntilAsync(() => manager.TryGetStableResource("combobreak", out _));

        Assert.True(manager.TryGetStableResource("combobreak", out var restoredBytes));
        Assert.Equal(expectedComboBreak, restoredBytes);
    }

    [Fact]
    public async Task OsuFolderPath_Changed_ReextractsStableDefaultResourcesFromNewFolder()
    {
        var osuFolderA = Directory.CreateDirectory(Path.Combine(_temporaryFolder, "osu-a")).FullName;
        var osuFolderB = Directory.CreateDirectory(Path.Combine(_temporaryFolder, "osu-b")).FullName;
        var comboBreakA = CreatePcmWave([0x11, 0x22, 0x33, 0x44]);
        var comboBreakB = CreatePcmWave([0x55, 0x66, 0x77, 0x88]);
        CreateSyntheticOsuGameplayDll(osuFolderA, new Dictionary<string, byte[]> { ["combobreak"] = comboBreakA });
        CreateSyntheticOsuGameplayDll(osuFolderB, new Dictionary<string, byte[]> { ["combobreak"] = comboBreakB });

        var appSettings = new AppSettings();
        appSettings.Paths.OsuFolderPath = osuFolderA;
        appSettings.Paths.AllowAutoLoadSkins = true;

        using var manager = CreateSkinManager(appSettings);

        await manager.ReloadSkinsAsync();
        await WaitUntilAsync(() =>
            manager.TryGetStableResource("combobreak", out var bytes) && bytes.SequenceEqual(comboBreakA));

        appSettings.Paths.OsuFolderPath = osuFolderB;
        await WaitUntilAsync(() =>
            manager.TryGetStableResource("combobreak", out var bytes) && bytes.SequenceEqual(comboBreakB));

        Assert.True(manager.TryGetStableResource("combobreak", out var updatedBytes));
        Assert.Equal(comboBreakB, updatedBytes);
    }

    public void Dispose()
    {
        Directory.Delete(_temporaryFolder, recursive: true);
    }

    private static SkinManager CreateSkinManager(AppSettings appSettings)
    {
        var syncSessionContext = new SyncSessionContext(appSettings);
        var coordinator = new GameSyncSourceCoordinator(
            syncSessionContext,
            [],
            NullLogger<GameSyncSourceCoordinator>.Instance);

        return new SkinManager(
            new ImmediateDispatcher(),
            new NoOpPersistence(),
            NullLogger<SkinManager>.Instance,
            appSettings,
            new AudioCacheManager(NullLogger<AudioCacheManager>.Instance),
            new ApplicationState(appSettings),
            coordinator,
            lazerSyncSource: null,
            syncSessionContext);
    }

    private static void CreateSyntheticOsuGameplayDll(string osuFolder, IReadOnlyDictionary<string, byte[]> resources)
    {
        byte[] resourceBytes;
        using (var resourceStream = new MemoryStream())
        {
            using (var writer = new ResourceWriter(resourceStream))
            {
                foreach (var (key, value) in resources)
                {
                    writer.AddResource(key, value);
                }

                writer.Generate();
            }

            resourceBytes = resourceStream.ToArray();
        }

        var assembly = new AssemblyDefUser("osu!gameplay", new Version(1, 0, 0, 0));
        var module = new ModuleDefUser("osu!gameplay.dll");
        assembly.Modules.Add(module);
        module.Kind = ModuleKind.Dll;
        module.Resources.Add(new EmbeddedResource(
            "osu_gameplay.ResourcesStore.resources",
            resourceBytes,
            ManifestResourceAttributes.Public));

        var dllPath = Path.Combine(osuFolder, "osu!gameplay.dll");
        module.Write(dllPath);
    }

    private static byte[] CreatePcmWave(byte[] pcmData)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8);
            writer.Write(36 + pcmData.Length);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)2);
            writer.Write(44100);
            writer.Write(44100 * 2 * sizeof(short));
            writer.Write((short)(2 * sizeof(short)));
            writer.Write((short)16);
            writer.Write("data"u8);
            writer.Write(pcmData.Length);
            writer.Write(pcmData);
        }

        return stream.ToArray();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, cancellation.Token);
        }
    }

    private sealed class ImmediateDispatcher : IApplicationDispatcher
    {
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpPersistence : IAppSettingsPersistence
    {
        public void Save()
        {
        }
    }
}
