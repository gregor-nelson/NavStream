using System.IO.Pipes;
using System.Text.Json;

namespace MpvGrid;

/// <summary>
/// Render-side end of the control IPC (D-DASH-1). Runs a background accept loop on a named pipe:
/// when the (single) dashboard connects, it pushes a <see cref="ControlSnapshot"/> every second
/// (plus on demand via <see cref="PushNow"/>) and reads <see cref="ControlCommand"/> lines, handing
/// each to the supplied handler. The handler is expected to marshal onto the render UI thread.
///
/// Lifetime: a background thread that dies with the render process. A "restart display" simply lets
/// the render process exit; the new render spins up a fresh server on the same pipe name and the
/// dashboard client reconnects. Nothing here ever throws into the render app.
/// </summary>
internal sealed class ControlServer : IDisposable
{
    private readonly Func<ControlSnapshot> _snapshot;
    private readonly Action<ControlCommand> _onCommand;
    private readonly Thread _thread;
    private volatile bool _stop;

    private readonly object _writeGate = new();
    private StreamWriter? _writer;   // non-null only while a client is connected

    public ControlServer(Func<ControlSnapshot> snapshot, Action<ControlCommand> onCommand)
    {
        _snapshot = snapshot;
        _onCommand = onCommand;
        _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "control-server" };
    }

    public void Start() => _thread.Start();

    /// <summary>Push a telemetry frame immediately (e.g. when a feed's state flips). No-op if no client.</summary>
    public void PushNow()
    {
        try { Send(_snapshot()); } catch { /* never let telemetry crash the render */ }
    }

    private void AcceptLoop()
    {
        while (!_stop)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    Constants.ControlPipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                pipe.WaitForConnection();
                Logger.Log("Control: dashboard connected.");
                ServeClient(pipe);
            }
            catch (Exception ex)
            {
                if (!_stop) Logger.Log($"Control: accept loop error: {ex.Message}");
            }

            if (!_stop) Thread.Sleep(250); // brief pause before re-listening
        }
    }

    private void ServeClient(NamedPipeServerStream pipe)
    {
        var reader = new StreamReader(pipe);
        var writer = new StreamWriter(pipe) { AutoFlush = true };
        lock (_writeGate) _writer = writer;

        using var pushStop = new CancellationTokenSource();
        var pusher = new Thread(() =>
        {
            while (!pushStop.IsCancellationRequested && pipe.IsConnected)
            {
                PushNow();
                try { pushStop.Token.WaitHandle.WaitOne(1000); } catch { break; }
            }
        }) { IsBackground = true, Name = "control-pusher" };
        pusher.Start();

        try
        {
            string? line;
            while (!_stop && (line = reader.ReadLine()) != null)
            {
                ControlCommand? cmd = null;
                try { cmd = JsonSerializer.Deserialize<ControlCommand>(line, ControlJson.Opts); }
                catch (Exception ex) { Logger.Log($"Control: bad command line: {ex.Message}"); }
                if (cmd is null) continue;

                Logger.Log($"Control: command '{cmd.Name}' (idx={cmd.Index}, bool={cmd.BoolValue}, int={cmd.IntValue}).");
                try { _onCommand(cmd); }
                catch (Exception ex) { Logger.Log($"Control: command handler threw: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            if (!_stop) Logger.Log($"Control: client read ended: {ex.Message}");
        }
        finally
        {
            pushStop.Cancel();
            lock (_writeGate) _writer = null;
            Logger.Log("Control: dashboard disconnected.");
        }
    }

    private void Send(ControlSnapshot snapshot)
    {
        StreamWriter? w;
        lock (_writeGate) w = _writer;
        if (w is null) return;

        string json = JsonSerializer.Serialize(snapshot, ControlJson.Opts);
        lock (_writeGate)
        {
            if (_writer is null) return;
            try { _writer.WriteLine(json); }
            catch (Exception ex) { Logger.Log($"Control: push failed: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        _stop = true;
        // The accept thread is a background thread and the render process is exiting; we don't try to
        // unblock WaitForConnection() — it dies with the process.
    }
}
