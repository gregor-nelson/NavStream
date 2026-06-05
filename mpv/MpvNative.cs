using System.Globalization;
using System.Runtime.InteropServices;

namespace MpvGrid.Mpv;

/// <summary>
/// The raw libmpv C ABI (D1 — hand-rolled P/Invoke, no managed wrapper package). Every entry
/// point is <see cref="CallingConvention.Cdecl"/>; strings cross as UTF-8 (<see cref="UnmanagedType.LPUTF8Str"/>).
/// Nothing here knows about WinForms or the engine — <see cref="MpvClient"/> is the only consumer.
///
/// D2 native-load story: the static constructor registers the DllImport resolver that loads the pinned
/// <c>libmpv-2.dll</c>. It supports two deployment layouts, tried in order by <see cref="ResolveLibmpvPath"/>:
///   1. Loose next-to-exe — <c>AppContext.BaseDirectory\libmpv\win-x64\libmpv-2.dll</c> (dev builds and
///      <c>dotnet run</c>, where the csproj copies the dll into the output tree).
///   2. Single-file fallback — when no loose dll is present (single-file publish bundles it as an embedded
///      resource instead), the dll is self-extracted once to a size-keyed cache dir under %TEMP% and loaded
///      from there. This keeps a single-file <c>publish</c> a literal one-file exe.
/// This is the single resolver-registration site in the process; do not register another.
/// </summary>
internal static class MpvNative
{
    private const string Lib = "libmpv-2";
    private const string LibFile = "libmpv-2.dll";

    static MpvNative()
    {
        // First access to any member of this class runs this ctor (the explicit static ctor makes the
        // type NOT beforefieldinit, so it is guaranteed to run before the first DllImport call resolves
        // the library). That is exactly when the resolver must already be registered.
        NativeLibrary.SetDllImportResolver(typeof(MpvNative).Assembly, static (name, _, _) =>
            name == Lib ? NativeLibrary.Load(ResolveLibmpvPath()) : IntPtr.Zero);
    }

    /// <summary>Locate the libmpv dll: prefer the loose next-to-exe copy; otherwise self-extract the
    /// embedded copy (single-file publish) and return the cached path.</summary>
    private static string ResolveLibmpvPath()
    {
        string loose = Path.Combine(AppContext.BaseDirectory, "libmpv", "win-x64", LibFile);
        return File.Exists(loose) ? loose : ExtractEmbeddedLibmpv();
    }

    /// <summary>Extract the embedded <c>libmpv-2.dll</c> resource to a per-size cache under %TEMP% and
    /// return its path. Idempotent and re-launch-cheap: if a file of the exact embedded byte length already
    /// exists it is reused without rewriting. The write is atomic (unique temp file then move) so concurrent
    /// or crashed launches can never hand back a half-written dll.</summary>
    private static string ExtractEmbeddedLibmpv()
    {
        var asm = typeof(MpvNative).Assembly;
        using Stream res = asm.GetManifestResourceStream(LibFile)
            ?? throw new DllNotFoundException(
                $"No loose libmpv-2.dll beside the exe and no embedded '{LibFile}' resource to fall back on.");

        long len = res.Length;
        string dir = Path.Combine(Path.GetTempPath(), "MpvGrid", "libmpv-" + len.ToString(CultureInfo.InvariantCulture));
        string dest = Path.Combine(dir, LibFile);
        if (File.Exists(dest) && new FileInfo(dest).Length == len)
            return dest;

        Directory.CreateDirectory(dir);
        string tmp = Path.Combine(dir, $"{LibFile}.{Environment.ProcessId}.tmp");
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            res.CopyTo(fs);

        try
        {
            File.Move(tmp, dest, overwrite: true);
        }
        catch (IOException) when (File.Exists(dest))
        {
            // Another process extracted the same dll first; its copy is valid — drop ours and use it.
            try { File.Delete(tmp); } catch (IOException) { /* best-effort cleanup */ }
        }
        return dest;
    }

    // ---- mpv_format (client.h) ----
    public const int MPV_FORMAT_NONE = 0;
    public const int MPV_FORMAT_STRING = 1;
    public const int MPV_FORMAT_FLAG = 3;
    public const int MPV_FORMAT_INT64 = 4;
    public const int MPV_FORMAT_DOUBLE = 5;
    public const int MPV_FORMAT_NODE = 6;

    // ---- mpv_event_id (only the ones this app reacts to; full enum in client.h) ----
    public const int MPV_EVENT_NONE = 0;
    public const int MPV_EVENT_SHUTDOWN = 1;
    public const int MPV_EVENT_END_FILE = 7;
    public const int MPV_EVENT_FILE_LOADED = 8;
    public const int MPV_EVENT_PLAYBACK_RESTART = 21;
    public const int MPV_EVENT_PROPERTY_CHANGE = 22;

    // ---- mpv_end_file_reason ----
    public const int MPV_END_FILE_REASON_EOF = 0;
    public const int MPV_END_FILE_REASON_STOP = 2;
    public const int MPV_END_FILE_REASON_QUIT = 3;
    public const int MPV_END_FILE_REASON_ERROR = 4;
    public const int MPV_END_FILE_REASON_REDIRECT = 5;

    /// <summary>struct mpv_event — the header of every event returned by <see cref="mpv_wait_event"/>.
    /// 24 bytes on x64 (int+int+uint64+ptr); <c>data</c> points at an event-specific struct.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct mpv_event
    {
        public int event_id;
        public int error;
        public ulong reply_userdata;
        public IntPtr data;
    }

    /// <summary>struct mpv_event_property — <c>data</c> of a PROPERTY_CHANGE event. We read only the name
    /// here; the value is polled back via <c>mpv_get_property_*</c> so we never depend on the union layout.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct mpv_event_property
    {
        public IntPtr name;     // const char*
        public int format;      // mpv_format (padded to 8 before data on x64)
        public IntPtr data;
    }

    // ---- lifecycle ----
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint mpv_client_api_version();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_create();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_initialize(IntPtr ctx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_terminate_destroy(IntPtr ctx);

    /// <summary>Make a blocked <see cref="mpv_wait_event"/> return at once (latched — no lost-wakeup race).</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_wakeup(IntPtr ctx);

    // ---- options / properties ----
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_option_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string data);

    /// <summary>Returns a malloc'd UTF-8 string (NULL on error). Copy it, then release with <see cref="mpv_free"/>.</summary>
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_get_property_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    // mpv_get_property — one extern per native format (same C symbol, distinct out-types). The 'format'
    // arg MUST match the out parameter (INT64/DOUBLE/FLAG) or libmpv writes the wrong width.
    [DllImport(Lib, EntryPoint = "mpv_get_property", CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property_long(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out long data);

    [DllImport(Lib, EntryPoint = "mpv_get_property", CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property_double(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out double data);

    [DllImport(Lib, EntryPoint = "mpv_get_property", CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property_flag(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out int data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_free(IntPtr data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_observe_property(IntPtr ctx, ulong replyUserdata,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format);

    // ---- commands ----
    // args is a NULL-terminated array of UTF-8 C-strings. The managed IntPtr[] is passed as a pointer to
    // the first element; MpvClient builds the array (incl. the trailing IntPtr.Zero) and frees it.
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command(IntPtr ctx, IntPtr[] args);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_command_string(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string args);

    // ---- events / logging ----
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_request_log_messages(IntPtr ctx,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string minLevel);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mpv_error_string(int error);

    /// <summary>Human-readable text for an mpv error code (negative = failure).</summary>
    public static string ErrorString(int error) =>
        Marshal.PtrToStringUTF8(mpv_error_string(error)) ?? error.ToString();
}
