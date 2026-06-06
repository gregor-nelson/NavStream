using System.Diagnostics;
using System.Text;

namespace NavStream;

/// <summary>
/// Tiny append-only logger shared by the supervisor and render processes (spec §6, §8).
/// Both processes write to the same LogPath; each line is tagged with the role + PID so an
/// unattended post-mortem can tell supervisor restarts apart from per-feed reconnects.
/// Best-effort and never throws — logging must not be able to take the app down.
/// </summary>
internal static class Logger
{
    private static readonly object Gate = new();
    private static string _path = "navstream.log";
    private static string _tag = "?";
    private static int _pid;

    public static void Init(string path, string roleTag)
    {
        _path = path;
        _tag = roleTag;
        try { _pid = Process.GetCurrentProcess().Id; } catch { _pid = 0; }
    }

    public static void Log(string message)
    {
        // UTC-less local timestamp is fine for an operator reading the file on the box.
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{_tag}:{_pid}] {message}";
        lock (Gate)
        {
            // Retry briefly: the other process may hold the file for its own append.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var fs = new FileStream(_path, FileMode.Append, FileAccess.Write,
                        FileShare.ReadWrite);
                    using var sw = new StreamWriter(fs, Encoding.UTF8);
                    sw.WriteLine(line);
                    return;
                }
                catch
                {
                    Thread.Sleep(15);
                }
            }
        }
    }
}
