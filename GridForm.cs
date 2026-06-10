using System.Drawing;
using System.Windows.Forms;

namespace NavStream;

/// <summary>
/// One borderless render window of the wall (spec §3, D6) — one per <see cref="DisplayConfig"/> entry,
/// all hosted in the same render process. Hosts that display's slice of <see cref="MpvHost"/> controls
/// tiled via <see cref="GridLayout.Compute"/> (auto or a fixed layout preset; a fixed frame's unoccupied
/// cells show this form's black background). Owns no playback state itself — the single shared engine
/// embeds an mpv handle into each host's HWND, and the hotkey callbacks below carry global feed indices
/// so any focused form can drive any feed.
/// </summary>
internal sealed class GridForm : Form
{
    private readonly Config _config;
    private readonly DisplayConfig _display;
    private readonly int _displayIndex;
    private LayoutSpec _spec;
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

    public GridForm(Config config, DisplayConfig display, int displayIndex)
    {
        _config = config;
        _display = display;
        _displayIndex = displayIndex;
        _spec = LayoutSpec.Parse(display.Layout);

        FormBorderStyle = FormBorderStyle.None;
        Text = "NavStream";
        BackColor = Color.Black;
        ShowInTaskbar = false;
        KeyPreview = true;
        DoubleBuffered = false; // native video paints itself; no managed double-buffer needed
        StartPosition = FormStartPosition.Manual;
        TopMost = _config.AlwaysOnTop;

        Bounds = TargetScreenBounds(_display.Monitor);

        // One host per cell this display takes from the flat stream list (Normalize guarantees every view
        // gets a feed, except the residual-streams shortfall case which RenderApp logs at startup).
        _views = new MpvHost[display.Cells];
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

        // Compute returns exactly _views.Length rects (Normalize caps Cells at the layout's capacity);
        // a fixed frame's unoccupied capacity shows this form's black background.
        var rects = GridLayout.Compute(_spec, _views.Length, ClientSize.Width, ClientSize.Height);
        for (int i = 0; i < _views.Length; i++)
        {
            _views[i].Bounds = rects[i];
        }
        OnLayoutChanged?.Invoke();
    }

    /// <summary>Move this display's grid to another monitor live (dashboard Phase B). Clamps to a valid
    /// index, re-bounds to that screen's full pixel rect, relayouts the views, and (via ApplyGrid →
    /// OnLayoutChanged) repositions the overlay. RenderApp mirrors Config.Monitor from Displays[0] after
    /// dispatch. Must run on the UI thread.</summary>
    public void SetMonitor(int monitorIndex)
    {
        if (monitorIndex < 0) monitorIndex = 0;
        // Preserve the REQUESTED index in config even when that monitor isn't attached right now —
        // the bounds fall back to screen 0 (TargetScreenBounds), and the saved value means a replug +
        // restart restores the wall to the intended monitor (edge-case contract; the dashboard offers
        // saved-but-unplugged monitors as "(unplugged)" options on the same promise).
        _display.Monitor = monitorIndex;
        Bounds = TargetScreenBounds(monitorIndex);
        ApplyGrid();
        bool attached = monitorIndex < Screen.AllScreens.Length;
        Logger.Log($"Render: display {_displayIndex} moved to monitor {monitorIndex}"
            + (attached ? "." : " (not attached — showing on monitor 0 until it returns)."));
    }

    /// <summary>Switch this display's layout preset live (dashboard setLayout). Rejected (false) when the
    /// new layout can't hold this display's cells — partition changes go through the save+restart path
    /// instead. Must run on the UI thread.</summary>
    public bool SetLayout(LayoutSpec spec)
    {
        if (spec.Capacity < _views.Length)
        {
            Logger.Log($"Render: display {_displayIndex} layout '{spec}' rejected — capacity {spec.Capacity} < {_views.Length} cells.");
            return false;
        }
        _spec = spec;
        _display.Layout = spec.ToString();
        ApplyGrid();
        Logger.Log($"Render: display {_displayIndex} layout set to '{spec}'.");
        return true;
    }

    /// <summary>Toggle borderless full-bleed (None) vs a normal sizable window live (dashboard Phase B).
    /// Re-asserts the monitor bounds after the style change and relayouts. Must run on the UI thread.</summary>
    public void SetBorderless(bool borderless)
    {
        _config.Borderless = borderless;
        FormBorderStyle = borderless ? FormBorderStyle.None : FormBorderStyle.Sizable;
        ShowInTaskbar = !borderless;
        Bounds = TargetScreenBounds(_display.Monitor);
        ApplyGrid();
        Logger.Log($"Render: display {_displayIndex} borderless = {borderless}.");
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
        // Re-assert bounds after the window manager has had its say (DPI/monitor quirks). Each form
        // re-asserts only its own monitor, so multiple forms never fight over placement.
        Bounds = TargetScreenBounds(_display.Monitor);
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
