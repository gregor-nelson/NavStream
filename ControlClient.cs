using System.IO.Pipes;
using System.Text.Json;

namespace NavStream;

/// <summary>
/// Dashboard-side end of the control IPC (D-DASH-1). A background thread keeps a connection to the
/// render process's named pipe: it connects, streams <see cref="ControlSnapshot"/> frames (raising
/// <see cref="SnapshotReceived"/>), and exposes <see cref="Send"/> for commands. When the render
/// process restarts (or hasn't started yet) the connection drops; the loop reports it via
/// <see cref="ConnectionChanged"/> and retries every second, so the dashboard survives grid restarts.
///
/// All events fire on the client's background thread — the form marshals them onto its UI thread.
/// </summary>
internal sealed class ControlClient : IDisposable
{
    private readonly Thread _thread;
    private volatile bool _stop;

    private readonly object _writeGate = new();
    private StreamWriter? _writer;

    /// <summary>A telemetry frame arrived from the render process.</summary>
    public event Action<ControlSnapshot>? SnapshotReceived;
    /// <summary>Connection state changed (true = connected to the grid, false = grid down/reconnecting).</summary>
    public event Action<bool>? ConnectionChanged;

    public ControlClient()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "control-client" };
    }

    public void Start() => _thread.Start();

    /// <summary>Send a command to the render process. Returns false if not currently connected.</summary>
    public bool Send(ControlCommand cmd)
    {
        StreamWriter? w;
        lock (_writeGate) w = _writer;
        if (w is null) return false;

        string json = JsonSerializer.Serialize(cmd, ControlJson.Opts);
        lock (_writeGate)
        {
            if (_writer is null) return false;
            try { _writer.WriteLine(json); return true; }
            catch { return false; }
        }
    }

    private void Loop()
    {
        while (!_stop)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".", Constants.ControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

                pipe.Connect(1500); // throws if no server within the timeout

                var reader = new StreamReader(pipe);
                var writer = new StreamWriter(pipe) { AutoFlush = true };
                lock (_writeGate) _writer = writer;
                ConnectionChanged?.Invoke(true);

                string? line;
                while (!_stop && (line = reader.ReadLine()) != null)
                {
                    ControlSnapshot? snap = null;
                    try { snap = JsonSerializer.Deserialize<ControlSnapshot>(line, ControlJson.Opts); }
                    catch { /* skip a malformed frame */ }
                    if (snap is not null) SnapshotReceived?.Invoke(snap);
                }
            }
            catch { /* not connected yet / dropped — fall through to retry */ }
            finally
            {
                bool wasConnected;
                lock (_writeGate) { wasConnected = _writer is not null; _writer = null; }
                if (wasConnected) ConnectionChanged?.Invoke(false);
            }

            if (!_stop) Thread.Sleep(1000); // retry cadence
        }
    }

    public void Dispose() => _stop = true;
}
