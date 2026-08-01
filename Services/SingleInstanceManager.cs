using System.IO;
using System.IO.Pipes;
using System.Text;

namespace WebViewHub.Services;

/// <summary>
/// Ensures only one instance of the app runs at a time.
/// When a second instance starts, it sends its command-line args to
/// the running instance through a named pipe and exits.
/// </summary>
public class SingleInstanceManager : IDisposable
{
    private readonly string _mutexName;
    private readonly string _pipeName;
    private Mutex? _mutex;
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;

    public SingleInstanceManager(string name)
    {
        _mutexName = $@"Local\{name}.mutex";
        _pipeName = $"{name}.pipe";
    }

    public bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, name: _mutexName, out var createdNew);
        if (!createdNew)
        {
            _mutex.Dispose();
            _mutex = null;
        }
        return createdNew;
    }

    public void StartListening(Action<string> onCommand)
    {
        _listenerCts = new CancellationTokenSource();
        _listenerTask = Task.Run(() => ListenLoop(onCommand, _listenerCts.Token));
    }

    private async Task ListenLoop(Action<string> onCommand, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var command = await reader.ReadToEndAsync(ct);
                onCommand(command ?? "");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Pipe error — wait a moment and try again.
                try { await Task.Delay(500, ct); } catch { break; }
            }
        }
    }

    public void SendCommandToRunningInstance(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect(2000);

            using var writer = new StreamWriter(client, Encoding.UTF8);
            writer.Write(command);
            writer.Flush();
        }
        catch
        {
            // The other instance might be exiting — fail silently.
        }
    }

    public void Dispose()
    {
        try { _listenerCts?.Cancel(); } catch { }
        try { _listenerTask?.Wait(500); } catch { }
        _listenerCts?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
    }
}
