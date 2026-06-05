using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace MpvGrid;

/// <summary>
/// The localhost HTTP/SSE bridge that backs the browser control UI. It serves the embedded web assets
/// (HTML/JS/CSS/fonts), streams <see cref="ViewFrame"/>s over Server-Sent Events (<c>GET /events</c>),
/// and accepts operator actions as <see cref="ControlCommand"/>s (<c>POST /command</c>) which it hands to
/// the supplied dispatcher. It mirrors <see cref="ControlServer"/>: a background accept loop, best-effort
/// try/catch everywhere, nothing ever thrown into the host.
///
/// Security model (validated): bind <c>http://localhost:PORT/</c> only — http.sys grants a non-admin that
/// prefix with no urlacl/elevation, where <c>+</c>/<c>*</c>/<c>127.0.0.1</c> would throw Access Denied.
/// Every request is also rejected unless its remote endpoint is loopback. A per-launch GUID token gates
/// the meaningful routes — navigation (<c>/</c>), the telemetry stream (<c>/events</c>) and commands
/// (<c>/command</c>); the inert static sub-resources (app.js/style.css/fonts) are served to loopback
/// without a token since the browser fetches them without one and they expose nothing.
/// </summary>
internal sealed class HttpControlBridge : IDisposable
{
    private readonly SseHub _sse;
    private readonly Func<ControlCommand, bool> _onCommand;
    private readonly int _preferredPort;
    private readonly ConcurrentDictionary<string, byte[]> _assetCache = new();

    private HttpListener? _listener;
    private Thread? _thread;
    private volatile bool _stop;

    public string Token { get; } = Guid.NewGuid().ToString("N");
    public int Port { get; private set; }
    public string? BaseUrl { get; private set; }
    public bool IsRunning => BaseUrl is not null;

    public HttpControlBridge(SseHub sse, Func<ControlCommand, bool> onCommand, int preferredPort)
    {
        _sse = sse;
        _onCommand = onCommand;
        _preferredPort = preferredPort;
    }

    /// <summary>Bind a loopback port (the preferred one, else a short fallback scan — the bind <em>is</em> the
    /// probe) and start the accept loop. Returns false if nothing bound, so the host can run headless.</summary>
    public bool Start()
    {
        for (int p = _preferredPort; p < _preferredPort + 20; p++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{p}/");
            try
            {
                listener.Start();
                _listener = listener;
                Port = p;
                BaseUrl = $"http://localhost:{p}/";
                break;
            }
            catch
            {
                try { listener.Close(); } catch { }
            }
        }

        if (_listener is null)
        {
            Logger.Log($"Bridge: could not bind any loopback port in {_preferredPort}..{_preferredPort + 19} — running headless.");
            return false;
        }

        _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "http-bridge" };
        _thread.Start();
        Logger.Log($"Bridge: listening at {BaseUrl} (token gated).");

        // Diagnostic hook for automation/tests: when MPVGRID_BRIDGE_URLFILE points at a path, write the
        // full tokenized URL there so a test harness can reach the gated endpoints. Off by default, so the
        // token never lands in the normal log.
        try
        {
            string? urlFile = Environment.GetEnvironmentVariable("MPVGRID_BRIDGE_URLFILE");
            if (!string.IsNullOrEmpty(urlFile)) File.WriteAllText(urlFile, $"{BaseUrl}?token={Token}");
        }
        catch { }

        return true;
    }

    private void AcceptLoop()
    {
        while (!_stop)
        {
            HttpListenerContext ctx;
            try { ctx = _listener!.GetContext(); }
            catch { if (!_stop) continue; else break; }

            // Offload each request so a long-lived SSE connection never blocks the accept loop.
            ThreadPool.QueueUserWorkItem(_ => SafeHandle(ctx));
        }
    }

    private void SafeHandle(HttpListenerContext ctx)
    {
        try { Handle(ctx); }
        catch (Exception ex)
        {
            Logger.Log($"Bridge: request error: {ex.Message}");
            try { ctx.Response.Abort(); } catch { }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;

        // Defense-in-depth: localhost binding should already exclude remote callers, but reject any
        // non-loopback endpoint outright.
        if (req.RemoteEndPoint is null || !IPAddress.IsLoopback(req.RemoteEndPoint.Address))
        {
            Respond(ctx, 403, "text/plain", Bytes("forbidden"));
            return;
        }

        string path = req.Url?.AbsolutePath ?? "/";
        string method = req.HttpMethod;

        // ---- token-gated, meaningful routes ----
        if (path == "/events" && method == "GET")
        {
            if (!TokenOk(req)) { Respond(ctx, 403, "text/plain", Bytes("bad token")); return; }
            // Hand the still-open response to the hub; it owns the lifetime from here (do NOT close it).
            _sse.AddClient(ctx.Response);
            return;
        }

        if (path == "/command" && method == "POST")
        {
            if (!TokenOk(req)) { Respond(ctx, 403, "text/plain", Bytes("bad token")); return; }
            HandleCommand(ctx);
            return;
        }

        if (path == "/" && method == "GET")
        {
            if (!TokenOk(req)) { Respond(ctx, 403, "text/plain", Bytes("bad token")); return; }
            ServeAsset(ctx, "web.index.html", "text/html; charset=utf-8");
            return;
        }

        // ---- inert static sub-resources (loopback only, no token) ----
        if (method == "GET")
        {
            switch (path)
            {
                case "/app.js": ServeAsset(ctx, "web.app.js", "application/javascript; charset=utf-8"); return;
                case "/style.css": ServeAsset(ctx, "web.style.css", "text/css; charset=utf-8"); return;
                case "/favicon.ico": ServeAsset(ctx, "app.ico", "image/x-icon"); return;
            }
            if (path.StartsWith("/fonts/", StringComparison.Ordinal))
            {
                string file = path["/fonts/".Length..];
                if (IsSafeAssetName(file)) { ServeAsset(ctx, "fonts." + file, "font/ttf"); return; }
            }
        }

        Respond(ctx, 404, "text/plain", Bytes("not found"));
    }

    private void HandleCommand(HttpListenerContext ctx)
    {
        ControlCommand? cmd = null;
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
            string body = reader.ReadToEnd();
            cmd = JsonSerializer.Deserialize<ControlCommand>(body, ControlJson.Opts);
        }
        catch (Exception ex) { Logger.Log($"Bridge: bad command body: {ex.Message}"); }

        if (cmd is null || string.IsNullOrEmpty(cmd.Name))
        {
            Respond(ctx, 400, "application/json", Bytes("{\"ok\":false,\"error\":\"bad command\"}"));
            return;
        }

        bool ok;
        try { ok = _onCommand(cmd); }
        catch (Exception ex) { Logger.Log($"Bridge: command handler threw: {ex.Message}"); ok = false; }

        Respond(ctx, 200, "application/json", Bytes(ok ? "{\"ok\":true}" : "{\"ok\":false}"));
    }

    private bool TokenOk(HttpListenerRequest req)
    {
        string? t = req.QueryString["token"] ?? req.Headers["X-Token"];
        return string.Equals(t, Token, StringComparison.Ordinal);
    }

    /// <summary>Serve an embedded resource resolved by suffix match (robust to MSBuild id mangling), the
    /// same pattern as <see cref="AppIcon"/> / <see cref="Theme"/>. 404s if the asset isn't found.</summary>
    private void ServeAsset(HttpListenerContext ctx, string suffix, string contentType)
    {
        byte[]? data = LoadAsset(suffix);
        if (data is null) { Respond(ctx, 404, "text/plain", Bytes("not found")); return; }
        Respond(ctx, 200, contentType, data);
    }

    private byte[]? LoadAsset(string suffix)
    {
        if (_assetCache.TryGetValue(suffix, out var cached)) return cached;

        var asm = Assembly.GetExecutingAssembly();
        string? name = Array.Find(asm.GetManifestResourceNames(),
            n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;

        try
        {
            using var s = asm.GetManifestResourceStream(name);
            if (s is null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var bytes = ms.ToArray();
            _assetCache[suffix] = bytes;
            return bytes;
        }
        catch { return null; }
    }

    /// <summary>Guard the only path segment that comes from the URL: a plain <c>.ttf</c> filename — no path
    /// separators and no traversal — before it's used to resolve an embedded resource.</summary>
    private static bool IsSafeAssetName(string name) =>
        name.Length > 0
        && name.IndexOfAny(new[] { '/', '\\' }) < 0
        && !name.Contains("..")
        && name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase);

    private static void Respond(HttpListenerContext ctx, int status, string contentType, byte[] body)
    {
        try
        {
            var res = ctx.Response;
            res.StatusCode = status;
            res.ContentType = contentType;
            res.ContentLength64 = body.Length;
            res.OutputStream.Write(body, 0, body.Length);
            res.OutputStream.Close();
        }
        catch { try { ctx.Response.Abort(); } catch { } }
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    public void Dispose()
    {
        _stop = true;
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
    }
}
