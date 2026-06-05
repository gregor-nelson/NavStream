using System.Drawing;
using System.Windows.Forms;

namespace MpvGrid;

/// <summary>
/// The single borderless render window (spec §3, D6). Hosts N (1–16) <see cref="MpvHost"/> controls
/// tiled edge-to-edge with zero gaps via <see cref="GridLayout"/>. Owns no playback state itself —
/// the engine (step 4) embeds an mpv handle into each host's HWND and wires the hotkey callbacks below.
/// </summary>
internal sealed class GridForm : Form
{
    private readonly Config _config;
    private readonly MpvHost[] _views;
    private bool _viewsReady; // guards ApplyGrid() against resize events that fire mid-construction

    /// <summary>The video surfaces, row-major (TL→TR→…→BR); one per active stream (1–16). Engine embeds an mpv handle into each.</summary>
    public IReadOnlyList<MpvHost> Views => _views;

    // Wired by the engine/orchestrator in later steps; null-safe so step 3 runs standalone.
    // [DesignerSerializationVisibility.Hidden] keeps the WinForms analyzer (WFO1000) from trying
    // to code-serialize these delegate properties — this form is never opened in the designer.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<int>? ForceReconnect { get; set; }   // D1–D9 keys -> reconnect that feed (0-based index); cells 10–16 via dashboard
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action? ToggleOverlay { get; set; }         // H key
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action? OnLayoutChanged { get; set; }       // overlay repositioning hook

    public GridForm(Config config)
    {
        _config = config;

        FormBorderStyle = FormBorderStyle.None;
        Text = "MpvGrid";
        BackColor = Color.Black;
        ShowInTaskbar = false;
        KeyPreview = true;
        DoubleBuffered = false; // native video paints itself; no managed double-buffer needed
        StartPosition = FormStartPosition.Manual;
        TopMost = _config.AlwaysOnTop;

        Bounds = TargetScreenBounds(_config.Monitor);

        // One host per active stream (clamped to [1,16]); the grid auto-tiles to this count.
        int cells = Math.Clamp(_config.Streams.Count, 1, 16);
        _views = new MpvHost[cells];
        for (int i = 0; i < _views.Length; i++)
        {
            // MpvHost sets BackColor=Black + TabStop=false in its own ctor; its .Handle is the mpv embed target.
            var view = new MpvHost();
            _views[i] = view;
            Controls.Add(view);
        }

        _viewsReady = true;
        ApplyGrid();
    }

    /// <summary>Full pixel bounds of the chosen monitor (full-bleed, not the work area).</summary>
    private static Rectangle TargetScreenBounds(int monitorIndex)
    {
        var screens = Screen.AllScreens;
        if (screens.Length == 0)
            return new Rectangle(0, 0, 1920, 1080);
        if (monitorIndex < 0 || monitorIndex >= screens.Length)
            monitorIndex = 0;
        return screens[monitorIndex].Bounds;
    }

    /// <summary>Recompute the N-up tiling and place the views. Idempotent.</summary>
    private void ApplyGrid()
    {
        // OnResize can fire while the base Form ctor / Bounds assignment runs, before the views
        // exist. Bail until the MpvHosts are constructed (then the ctor calls us explicitly).
        if (!_viewsReady)
            return;

        var rects = GridLayout.Tile(_views.Length, ClientSize.Width, ClientSize.Height);
        for (int i = 0; i < _views.Length; i++)
        {
            _views[i].Bounds = rects[i];
        }
        OnLayoutChanged?.Invoke();
    }

    /// <summary>Move the whole grid to another monitor live (dashboard Phase B). Clamps to a valid index,
    /// re-bounds to that screen's full pixel rect, relayouts the 4 views, and (via ApplyGrid →
    /// OnLayoutChanged) repositions the overlay. Must run on the UI thread.</summary>
    public void SetMonitor(int monitorIndex)
    {
        var screens = Screen.AllScreens;
        if (monitorIndex < 0 || monitorIndex >= screens.Length) monitorIndex = 0;
        _config.Monitor = monitorIndex;
        Bounds = TargetScreenBounds(monitorIndex);
        ApplyGrid();
        Logger.Log($"Render: grid moved to monitor {monitorIndex}.");
    }

    /// <summary>Toggle borderless full-bleed (None) vs a normal sizable window live (dashboard Phase B).
    /// Re-asserts the monitor bounds after the style change and relayouts. Must run on the UI thread.</summary>
    public void SetBorderless(bool borderless)
    {
        _config.Borderless = borderless;
        FormBorderStyle = borderless ? FormBorderStyle.None : FormBorderStyle.Sizable;
        ShowInTaskbar = !borderless;
        Bounds = TargetScreenBounds(_config.Monitor);
        ApplyGrid();
        Logger.Log($"Render: borderless = {borderless}.");
    }

    /// <summary>Toggle always-on-top live (dashboard Phase B). Must run on the UI thread.</summary>
    public void SetAlwaysOnTop(bool onTop)
    {
        _config.AlwaysOnTop = onTop;
        TopMost = onTop;
        Logger.Log($"Render: always-on-top = {onTop}.");
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ApplyGrid();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // Re-assert bounds after the window manager has had its say (DPI/monitor quirks).
        Bounds = TargetScreenBounds(_config.Monitor);
        ApplyGrid();
        Activate();
    }

    // Command-key handling catches hotkeys before child controls. Native video HWNDs can steal
    // keyboard focus; ProcessCmdKey is the most reliable managed path for these. (Fallback if a
    // feed grabs focus on the target box: a low-level keyboard hook — see spec §6 fallbacks.)
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.Shift | Keys.Q:
                // Deliberate full stop — tell the supervisor to exit too (§3).
                Logger.Log("Render: Ctrl+Shift+Q -> full stop requested.");
                Environment.Exit(Constants.ExitStop);
                return true;

            case Keys.Escape:
                // Quit -> supervisor relaunches (= restart), per §3.
                Logger.Log("Render: Esc -> quit (supervisor will restart).");
                Close();
                return true;

            case Keys.H:
                ToggleOverlay?.Invoke();
                return true;

            case Keys.D1: case Keys.NumPad1: ForceReconnect?.Invoke(0); return true;
            case Keys.D2: case Keys.NumPad2: ForceReconnect?.Invoke(1); return true;
            case Keys.D3: case Keys.NumPad3: ForceReconnect?.Invoke(2); return true;
            case Keys.D4: case Keys.NumPad4: ForceReconnect?.Invoke(3); return true;
            case Keys.D5: case Keys.NumPad5: ForceReconnect?.Invoke(4); return true;
            case Keys.D6: case Keys.NumPad6: ForceReconnect?.Invoke(5); return true;
            case Keys.D7: case Keys.NumPad7: ForceReconnect?.Invoke(6); return true;
            case Keys.D8: case Keys.NumPad8: ForceReconnect?.Invoke(7); return true;
            case Keys.D9: case Keys.NumPad9: ForceReconnect?.Invoke(8); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
