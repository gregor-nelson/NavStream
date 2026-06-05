using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace MpvGrid;

/// <summary>
/// The LAN HTTP/SSE bridge that backs the browser control UI. It serves the embedded web assets
/// (HTML/JS/CSS/fonts), streams <see cref="ViewFrame"/>s over Server-Sent Events (<c>GET /events</c>),
/// and accepts operator actions as <see cref="ControlCommand"/>s (<c>POST /command</c>) which it hands to
/// the supplied dispatcher. It mirrors <see cref="ControlServer"/>: a background accept loop, best-effort
/// try/catch everywhere, nothing ever thrown into the host.
///
/// Network model: binds the wildcard prefix <c>http://+:PORT/</c> so the panel is reachable from any device
/// on the local network at <c>http://&lt;this-pc-ip&gt;:PORT</c> (the local machine still uses
/// <c>http://localhost:PORT</c>). The wildcard bind and the inbound firewall rule both need admin, which is
/// why the process runs elevated (see app.manifest); on a successful bind we self-register the firewall rule
/// (<see cref="TryOpenFirewall"/>) so there's nothing to configure by hand.
///
/// Access control: this is a trusted-LAN convenience with NO token and NO TLS. The only gate is the source
/// address — <see cref="IsLanClient"/> rejects any caller that isn't loopback or a private/link-local LAN
/// address, so a public NIC on the same box can't reach it. Anyone on the local network, however, has full
/// control of the grid. Acceptable for a private operator network; do not expose this to an untrusted one.
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

    public int Port { get; private set; }
    public string? BaseUrl { get; private set; }
    public bool IsRunning => BaseUrl is not null;

    public HttpControlBridge(SseHub sse, Func<ControlCommand, bool> onCommand, int preferredPort)
    {
        _sse = sse;
        _onCommand = onCommand;
        _preferredPort = preferredPort;
    }

    /// <summary>Bind the fixed wildcard LAN port (<c>http://+:PORT/</c>) and start the accept loop. The port
    /// is pinned (no scan) so the panel's URL is stable and type-able from other devices. Requires admin for
    /// the wildcard bind; on success we also open the inbound firewall. Returns false if the bind fails, so
    /// the host can run headless (the tray menu still works).</summary>
    public bool Start()
    {
        int p = _preferredPort;
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{p}/");
        try
        {
            listener.Start();
            _listener = listener;
            Port = p;
            // The local machine opens the panel via loopback; remote devices use http://<this-pc-ip>:PORT.
            BaseUrl = $"http://localhost:{p}/";
        }
        catch (Exception ex)
        {
            try { listener.Close(); } catch { }
            Logger.Log($"Bridge: could not bind http://+:{p}/ ({ex.Message}) — running headless. " +
                       "Is the port in use, or is the app not elevated?");
            return false;
        }

        TryOpenFirewall(p);

        _thread = new Thread(AcceptLoop) { IsBackground = true, Name = "http-bridge" };
        _thread.Start();
        Logger.Log($"Bridge: listening on http://+:{p}/ — reachable on the LAN at http://<this-pc-ip>:{p} (no token).");

        // Diagnostic hook for automation/tests: when MPVGRID_BRIDGE_URLFILE points at a path, write the
        // panel URL there so a test harness can reach the endpoints.
        try
        {
            string? urlFile = Environment.GetEnvironmentVariable("MPVGRID_BRIDGE_URLFILE");
            if (!string.IsNullOrEmpty(urlFile)) File.WriteAllText(urlFile, BaseUrl);
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

        // The only access gate (no token): accept loopback + private/link-local LAN callers, reject anything
        // public — so a public NIC on this box can't reach the panel even though we bind the wildcard prefix.
        if (!IsLanClient(req.RemoteEndPoint?.Address))
        {
            Respond(ctx, 403, "text/plain", Bytes("forbidden"));
            return;
        }

        string path = req.Url?.AbsolutePath ?? "/";
        string method = req.HttpMethod;

        // ---- meaningful routes ----
        if (path == "/events" && method == "GET")
        {
            // Hand the still-open response to the hub; it owns the lifetime from here (do NOT close it).
            _sse.AddClient(ctx.Response);
            return;
        }

        if (path == "/command" && method == "POST")
        {
            HandleCommand(ctx);
            return;
        }

        if (path == "/" && method == "GET")
        {
            ServeAsset(ctx, "web.index.html", "text/html; charset=utf-8");
            return;
        }

        // ---- static sub-resources ----
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

    /// <summary>The sole access gate: true for loopback and private/link-local LAN sources, false for public
    /// ones. IPv4-mapped IPv6 callers (common on a dual-stack wildcard bind) are unwrapped to their v4 form
    /// first so a remote <c>::ffff:192.168.x.x</c> is judged on its real address.</summary>
    private static bool IsLanClient(IPAddress? addr)
    {
        if (addr is null) return false;
        if (IPAddress.IsLoopback(addr)) return true;

        var a = addr.IsIPv4MappedToIPv6 ? addr.MapToIPv4() : addr;

        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = a.GetAddressBytes();
            if (b[0] == 10) return true;                              // 10.0.0.0/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true; // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;             // 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254) return true;             // 169.254.0.0/16 link-local
            return false;
        }

        if (a.AddressFamily == AddressFamily.InterNetworkV6)
            return a.IsIPv6LinkLocal || a.IsIPv6UniqueLocal;         // fe80::/10, fc00::/7

        return false;
    }

    /// <summary>Best-effort: ensure a single inbound TCP allow rule for our port exists, keyed by a stable
    /// name so it's idempotent (delete-then-add). Needs admin — which we have (see app.manifest). Runs netsh
    /// silently; any failure is logged and ignored (the listener still works on a network where the firewall
    /// already permits the port, e.g. one configured by a prior run).</summary>
    private static void TryOpenFirewall(int port)
    {
        const string ruleName = "MpvGrid Control Panel";
        try
        {
            RunNetsh($"advfirewall firewall delete rule name=\"{ruleName}\"");
            RunNetsh($"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow " +
                     $"protocol=TCP localport={port} profile=any");
            Logger.Log($"Bridge: ensured inbound firewall rule '{ruleName}' for TCP {port}.");
        }
        catch (Exception ex)
        {
            Logger.Log($"Bridge: could not set firewall rule for TCP {port}: {ex.Message}");
        }
    }

    private static void RunNetsh(string arguments)
    {
        var psi = new ProcessStartInfo("netsh", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi);
        proc?.WaitForExit(5000);
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
