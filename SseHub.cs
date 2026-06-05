using System.Net;

namespace MpvGrid;

/// <summary>
/// Holds the set of connected Server-Sent-Events clients (one per open browser tab) and fans a single
/// pre-serialized frame out to all of them. The <see cref="ViewFrameBuilder"/> is the only writer and it
/// serializes its calls, so the work here is just: keep the response streams, push the same bytes to each,
/// and drop any client whose write throws — a failed write is exactly how we learn a tab was closed.
///
/// A new tab shouldn't stare at a blank page for up to a second, so the last broadcast frame is cached and
/// replayed to each client the instant it connects. All access is under one lock, which also guarantees an
/// in-flight broadcast can't interleave its bytes with the initial replay to a just-added client.
/// </summary>
internal sealed class SseHub : IDisposable
{
    private readonly object _gate = new();
    private readonly List<Client> _clients = new();
    private byte[]? _lastFrame;
    private bool _disposed;

    /// <summary>Register a freshly-accepted SSE request: write the streaming headers, keep its response open
    /// (the bridge must NOT close it), and replay the most recent frame so the page renders immediately.</summary>
    public void AddClient(HttpListenerResponse response)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.KeepAlive = true;
        response.SendChunked = true;

        var client = new Client(response);
        lock (_gate)
        {
            if (_disposed) { client.Dispose(); return; }
            _clients.Add(client);
            if (_lastFrame is not null && !client.TryWrite(_lastFrame))
            {
                _clients.Remove(client);
                client.Dispose();
            }
        }
    }

    /// <summary>Push one already-framed SSE payload to every connected client; cache it for new arrivals.
    /// Dropped clients (write threw) are removed and disposed.</summary>
    public void Broadcast(byte[] frame)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _lastFrame = frame;
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                if (!_clients[i].TryWrite(frame))
                {
                    _clients[i].Dispose();
                    _clients.RemoveAt(i);
                }
            }
        }
    }

    public int ClientCount { get { lock (_gate) return _clients.Count; } }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            foreach (var c in _clients) c.Dispose();
            _clients.Clear();
        }
    }

    private sealed class Client : IDisposable
    {
        private readonly HttpListenerResponse _response;
        private readonly Stream _stream;

        public Client(HttpListenerResponse response)
        {
            _response = response;
            _stream = response.OutputStream;
        }

        public bool TryWrite(byte[] bytes)
        {
            try { _stream.Write(bytes, 0, bytes.Length); _stream.Flush(); return true; }
            catch { return false; }
        }

        public void Dispose()
        {
            try { _stream.Dispose(); } catch { }
            try { _response.Abort(); } catch { }
        }
    }
}
