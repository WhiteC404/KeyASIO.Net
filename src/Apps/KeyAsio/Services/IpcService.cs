using System.IO.Pipes;
using Avalonia.Controls;
using Avalonia.Threading;
using KeyAsio.Configuration;
using KeyAsio.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KeyAsio.Services;

public class IpcService : BackgroundService
{
    private readonly ILogger<IpcService> _logger;
    private readonly AppSettings _appSettings;
    public const string PipeName = "KeyAsio.Net.Pipe";

    public IpcService(ILogger<IpcService> logger, AppSettings appSettings)
    {
        _logger = logger;
        _appSettings = appSettings;
    }

    protected override async Task ExecuteAsync(CancellationToken token)
    {
        if (_appSettings.General.AllowMultipleInstance)
        {
            _logger.LogDebug("AllowMultipleInstance is enabled; skipping single-instance IPC pipe listener.");
            return;
        }

        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(token);

                using var reader = new StreamReader(server);
                var message = await reader.ReadToEndAsync(token);

                if (message == "SHOW_WINDOW")
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        var mainWindow = AppExtensions.CurrentDesktop?.MainWindow;
                        if (mainWindow != null)
                        {
                            mainWindow.Show();
                            mainWindow.Activate();
                            if (mainWindow.WindowState == WindowState.Minimized)
                            {
                                mainWindow.WindowState = WindowState.Normal;
                            }
                        }
                    });
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                _logger.LogInformation(ex, "Single-instance IPC pipe is already owned by another KeyASIO instance; stopping listener.");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in IPC Service");
                await Task.Delay(1000, token);
            }
        }
    }
}