using KeyAsio.Configuration;
using KeyAsio.Sync;
using KeyAsio.Sync.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace KeyAsio.UnitTests;

public sealed class MemorySyncBridgeTests
{
    [Fact]
    public async Task ReenablingSyncWhileStopping_RestartsTheSource()
    {
        var settings = new AppSettings();
        var context = new SyncSessionContext(settings);
        var stopRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Mock<IGameSyncSource>();
        var startCount = 0;
        source.Setup(value => value.Start()).Callback(() =>
        {
            if (Interlocked.Increment(ref startCount) == 2)
                restarted.TrySetResult();
        });
        source.Setup(value => value.StopAsync()).Returns(() =>
        {
            stopRequested.SetResult();
            return allowStop.Task;
        });
        var coordinator = new GameSyncSourceCoordinator(context, [source.Object],
            NullLogger<GameSyncSourceCoordinator>.Instance);
        using var lazer = new LazerIpcBridge(NullLogger<LazerIpcBridge>.Instance);
        var lazerSource = new LazerIpcGameSyncSource(lazer);
        var stable = new StableMemoryGameSyncSource(new MemoryScan(NullLogger<MemoryScan>.Instance));
        using var bridge = new MemorySyncBridge(coordinator, stable, lazerSource, context, settings,
            NullLogger<MemorySyncBridge>.Instance);
        var cancellationToken = TestContext.Current.CancellationToken;

        bridge.Start();
        settings.Sync.EnableSync = false;
        try
        {
            await stopRequested.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            settings.Sync.EnableSync = true;
        }
        finally
        {
            allowStop.SetResult();
        }

        await restarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        source.Verify(value => value.Start(), Times.Exactly(2));
    }

    [Fact]
    public async Task PartialStartFailure_RollsBackAlreadyStartedSources()
    {
        var settings = new AppSettings();
        var context = new SyncSessionContext(settings);
        var rollbackCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var primarySource = new Mock<IGameSyncSource>();
        primarySource.SetupGet(value => value.Priority).Returns(100);
        primarySource.SetupGet(value => value.Name).Returns("primary");
        primarySource.Setup(value => value.StopAsync()).Returns(() =>
        {
            rollbackCompleted.TrySetResult();
            return Task.CompletedTask;
        });

        var failingSource = new Mock<IGameSyncSource>();
        failingSource.SetupGet(value => value.Priority).Returns(0);
        failingSource.SetupGet(value => value.Name).Returns("failing");
        failingSource.Setup(value => value.Start()).Throws(new InvalidOperationException("Simulated start failure"));
        failingSource.Setup(value => value.StopAsync()).Returns(Task.CompletedTask);

        var coordinator = new GameSyncSourceCoordinator(context, [primarySource.Object, failingSource.Object],
            NullLogger<GameSyncSourceCoordinator>.Instance);
        using var lazer = new LazerIpcBridge(NullLogger<LazerIpcBridge>.Instance);
        var lazerSource = new LazerIpcGameSyncSource(lazer);
        var stable = new StableMemoryGameSyncSource(new MemoryScan(NullLogger<MemoryScan>.Instance));
        using var bridge = new MemorySyncBridge(coordinator, stable, lazerSource, context, settings,
            NullLogger<MemorySyncBridge>.Instance);

        bridge.Start();

        await rollbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        primarySource.Verify(value => value.Start(), Times.Once);
        primarySource.Verify(value => value.StopAsync(), Times.Once);
    }
}
