using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NavStream;

/// <summary>
/// Health overlay (spec §6), styled to InterMoor brand. A single monitor-sized top-level form that is
/// layered, click-through (WS_EX_TRANSPARENT) and non-activating (WS_EX_NOACTIVATE), so it draws reliably
/// *above* the native video HWNDs without an airspace fight and never steals keyboard focus from the grid.
///
/// Rendering uses a per-pixel-alpha layered window (<see cref="UpdateLayeredWindow"/>) rather than a
/// color-key TransparencyKey: this gives genuine translucency over the live video, soft drop shadows, and
/// anti-aliased rounded corners — none of which a color-key can produce (every non-key pixel is forced
/// opaque, and AA edges fringe against the key color). We render the whole overlay into a 32bpp ARGB
/// bitmap and push it to the compositor; WinForms' own WM_PAINT is suppressed.
///
/// Each quadrant corner shows one InterMoor-styled badge:
///  • Healthy  → an ambient LED dot + faint cell number, so good feeds stay out of the picture.
///  • Otherwise → a frosted-glass pill: brand-blue number badge, status LED, and a status line.
/// Toggle the whole overlay with H.
/// </summary>
internal sealed class OverlayForm : Form
{
    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x80;

    // ---- InterMoor brand palette (sampled from the corporate logo) ----
    private static readonly Color BrandDeep = Color.FromArgb(0, 70, 182);    // #0046B6 background blue
    private static readonly Color BrandAccent = Color.FromArgb(0, 112, 184); // #0070B8 ray / wordmark blue
    private static readonly Color BrandAccentHi = Color.FromArgb(40, 150, 224); // lifted accent for gradients

    // ---- Status LED colors (semantic; kept distinct from brand so health reads at a glance) ----
    private static readonly Color LedHealthy = Color.FromArgb(38, 199, 120);  // green
    private static readonly Color LedWarn = Color.FromArgb(245, 169, 35);     // amber
    private static readonly Color LedDown = Color.FromArgb(229, 72, 77);      // red
    private static readonly Color LedConnecting = Color.FromArgb(64, 156, 255); // brand-leaning blue

    // ---- Pill surface ----
    private static readonly Color PillFill = Color.FromArgb(196, 14, 17, 24);  // frosted near-black @ ~0.77
    private static readonly Color PillBorder = Color.FromArgb(64, 255, 255, 255);
    private static readonly Color TextPrimary = Color.FromArgb(245, 245, 248);
    private static readonly Color TextMuted = Color.FromArgb(165, 176, 192);

    // ---- Badge appearance (dashboard "Overlay settings"; live + persisted) ----
    // Live knobs the dashboard tunes via the SetBadge* setters (see RenderApp); defaults reproduce the prior
    // hardcoded look: pill fill α196 (= 77% opacity), 1.0× size, top-left corner. Opacity scales only the
    // frosted-glass *surface* (fill/shadow/sheen/border) in proportion — text, LEDs and the number badge stay
    // full strength. Size multiplies the master geometry scale; Position picks the anchor corner.
    private const float DefaultBadgeOpacity = 0.77f;  // 196/255 — the prior PillFill alpha
    private float _badgeOpacity = DefaultBadgeOpacity; // 0.30..1.0 (dashboard 30–100%)
    private float _badgeScale = 1f;                    // 0.60..1.60 (dashboard 60–160%)
    private int _badgeCorner;                          // 0=top-left (default), 1=top-right, 2=bottom-right, 3=bottom-left

    private IReadOnlyList<FeedController> _feeds = Array.Empty<FeedController>();
    private Rectangle[] _quadrants = Array.Empty<Rectangle>();

    public OverlayForm(Form owner, Rectangle screenBounds, bool topMost)
    {
        Owner = owner;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Bounds = screenBounds;
        RebuildQuadrants();   // _feeds is empty here → 1 rect; SetFeeds/Reposition rebuild before anything paints
        TopMost = topMost;
        Enabled = false;                // never takes input
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    // We own every pixel via UpdateLayeredWindow — suppress WinForms background/foreground painting.
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (Visible) RenderNow();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) RenderNow();
    }

    /// <summary>Rebuild the badge rects to match the active feed count (1–16), tiled like the video grid.
    /// Driven by <c>_feeds.Count</c> so badges always align with the cells <see cref="PaintOverlay"/> draws.</summary>
    private void RebuildQuadrants() =>
        _quadrants = GridLayout.Tile(Math.Clamp(_feeds.Count, 1, 16), Bounds.Width, Bounds.Height);

    /// <summary>Position the overlay over the grid and recompute the badge rects.</summary>
    public void Reposition(Rectangle screenBounds)
    {
        Bounds = screenBounds;
        RebuildQuadrants();
        RenderNow();
    }

    public void SetFeeds(IReadOnlyList<FeedController> feeds)
    {
        _feeds = feeds;
        RebuildQuadrants();
        RenderNow();
    }

    public void Toggle() => Visible = !Visible;

    /// <summary>Show/hide the brand watermark independently of the badges (dashboard "Brand logo" switch).
    /// Repaints on change. The watermark still shares the overlay's own visibility (the H toggle).</summary>
    public void SetLogoVisible(bool show)
    {
        if (_showLogo == show) return;
        _showLogo = show;
        RenderNow();
    }
    private bool _showLogo = true;

    /// <summary>Live-tune the brand watermark from the dashboard "Logo settings" controls. Each guards on an
    /// unchanged value then repaints, mirroring <see cref="SetLogoVisible"/>. Values arrive pre-mapped to
    /// render units (RenderApp maps the dashboard percent/enum): opacity and size are 0..1 fractions,
    /// brightness a 0..0.5 RGB lift, corner 0..3 (0=BL, 1=BR, 2=TR, 3=TL).</summary>
    public void SetLogoOpacity(float opacity)
    {
        if (_opacity == opacity) return;
        _opacity = opacity;
        RenderNow();
    }

    public void SetLogoBrightness(float brightness)
    {
        if (_brightness == brightness) return;
        _brightness = brightness;
        RenderNow();
    }

    public void SetLogoSize(float widthFrac)
    {
        if (_widthFrac == widthFrac) return;
        _widthFrac = widthFrac;
        RenderNow();
    }

    public void SetLogoPosition(int corner)
    {
        if (_logoCorner == corner) return;
        _logoCorner = corner;
        RenderNow();
    }

    /// <summary>Live-tune the health overlay badges from the dashboard "Overlay settings" controls. Each guards
    /// on an unchanged value then repaints, mirroring <see cref="SetLogoOpacity"/>. Values arrive pre-mapped to
    /// render units (RenderApp maps the dashboard percent/enum): opacity &amp; size are fractions, corner 0..3
    /// (0=TL, 1=TR, 2=BR, 3=BL).</summary>
    public void SetBadgeOpacity(float opacity)
    {
        if (_badgeOpacity == opacity) return;
        _badgeOpacity = opacity;
        RenderNow();
    }

    public void SetBadgeSize(float scale)
    {
        if (_badgeScale == scale) return;
        _badgeScale = scale;
        RenderNow();
    }

    public void SetBadgePosition(int corner)
    {
        if (_badgeCorner == corner) return;
        _badgeCorner = corner;
        RenderNow();
    }

    // ---------------------------------------------------------------- render pipeline

    /// <summary>Rebuild the overlay bitmap and push it to the compositor. Must run on the UI thread.</summary>
    public void RenderNow()
    {
        if (!Visible || !IsHandleCreated) return;
        int w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAlias; // ClearType is invalid on an alpha surface
            g.Clear(Color.Transparent);
            PaintOverlay(g);
        }
        Premultiply(bmp);
        PushLayered(bmp);
    }

    private void PaintOverlay(Graphics g)
    {
        DrawWatermark(g);   // station brand, behind the per-feed badges
        for (int i = 0; i < _feeds.Count && i < _quadrants.Length; i++)
            DrawBadge(g, _quadrants[i], _feeds[i]);
    }

    // ---------------------------------------------------------------- brand watermark

    // Per-cell watermark tuning. Width-fraction, opacity, brightness and corner are live fields the dashboard
    // tunes via the "Logo settings" controls (see RenderApp + the SetLogo* setters); the px clamps stay
    // compile-time. Defaults reproduce the prior hardcoded look: 15% width, 0.40 alpha, 0.25 RGB lift
    // (= brightness 50% in the dashboard) and bottom-left placement.
    private const float WatermarkMinWidth = 64f;
    private const float WatermarkMaxWidth = 240f;
    private float _widthFrac = 0.15f;    // fraction of cell width (dashboard 5–30%)
    private float _opacity = 0.40f;      // alpha multiplier — subtle, but readable on every cell
    private float _brightness = 0.25f;   // RGB lift toward white (0..0.5) so the dark-blue mark reads over live video
    private int _logoCorner;             // 0=bottom-left (default), 1=bottom-right, 2=top-right, 3=top-left

    /// <summary>Paint the InterMoor logo as a faint watermark in a chosen corner of every cell (default
    /// bottom-left, mirroring where the health badge sits). Size, opacity, brightness and corner are live
    /// dashboard knobs (the "Logo settings" controls); the watermark scales with the tiling from 1-up to
    /// 16-up and is drawn at low opacity + an RGB lift via a <see cref="ColorMatrix"/>. Shares the overlay's
    /// visibility (the H toggle) and the dashboard "Brand logo" switch.</summary>
    private void DrawWatermark(Graphics g)
    {
        if (!_showLogo) return;
        var logo = LogoImage.Default;
        if (logo is null) return;

        using var attrs = new ImageAttributes();
        // Scale source alpha (keeps the watermark subtle) and lift RGB toward white so the dark-blue mark
        // reads brighter over live video. The translation row (Matrix4x) adds a constant to each channel;
        // GDI+ clamps to [0,1]. Fully-transparent pixels stay invisible (Premultiply zeros a==0).
        var cm = new ColorMatrix
        {
            Matrix33 = _opacity,
            Matrix40 = _brightness,
            Matrix41 = _brightness,
            Matrix42 = _brightness,
        };
        attrs.SetColorMatrix(cm);

        // Size every cell's watermark off the *standard* cell width so the logo is one uniform size across the
        // whole grid. Irregular layouts (e.g. 2-over-1) stretch the last row's cell edge-to-edge, so keying the
        // size off each cell's own width would balloon that wide cell's logo. The narrowest quad == width/cols
        // (the regular column); remainder-row cells are only ever wider, never narrower.
        float refWidth = _quadrants.Length > 0 ? _quadrants[0].Width : 0;
        for (int i = 1; i < _quadrants.Length; i++)
            if (_quadrants[i].Width < refWidth) refWidth = _quadrants[i].Width;
        float baseW = Math.Clamp(refWidth * _widthFrac, WatermarkMinWidth, WatermarkMaxWidth);

        foreach (var quad in _quadrants)
        {
            float w = baseW;
            if (w > quad.Width * 0.4f) w = quad.Width * 0.4f;   // never dominate a tiny cell
            float h = w * logo.Height / logo.Width;
            float inset = Math.Clamp(quad.Height / 540f, 0.85f, 2.2f) * 16f;  // match the badge inset

            // Corner placement (0=BL default, 1=BR, 2=TR, 3=TL). TL deliberately overlaps the health badge.
            float x = (_logoCorner == 1 || _logoCorner == 2) ? quad.Right - inset - w : quad.X + inset;
            float y = (_logoCorner == 2 || _logoCorner == 3) ? quad.Y + inset : quad.Bottom - inset - h;

            g.DrawImage(logo, new Rectangle((int)x, (int)y, (int)w, (int)h),
                0, 0, logo.Width, logo.Height, GraphicsUnit.Pixel, attrs);
        }
    }

    // ---------------------------------------------------------------- badge drawing

    private void DrawBadge(Graphics g, Rectangle quad, FeedController feed)
    {
        // Scale all geometry to the quadrant so badges stay proportional from 1080p to 4K and beyond, then by
        // the dashboard "Overlay settings" Size knob (_badgeScale). inset = 16*scale rides along, so a larger
        // badge sits a touch further from its corner. Each renderer measures its pill then anchors it to the
        // chosen corner (Position) via AnchorBadge(), so badge widths can stay dynamic.
        float scale = Math.Clamp(quad.Height / 540f, 0.85f, 2.2f) * _badgeScale;
        float inset = 16f * scale;
        float maxWidth = quad.Width - 2f * inset;   // keep a long vessel name inside its own quadrant

        Color led = LedColor(feed.Health);
        string name = (feed.Name ?? string.Empty).Trim();

        // "Name only" mode (per feed): collapse to just the vessel name + a health dot — no cell badge,
        // bitrate, lost-frame count or status word. A blank name falls back to "Cell N" so it's still labelled.
        if (feed.NamesOnly)
        {
            string label = name.Length > 0 ? name : "Cell " + feed.CellNumber.ToString(CultureInfo.InvariantCulture);
            DrawNamePill(g, quad, inset, scale, maxWidth, led, label);
            return;
        }

        // Healthy *and* unnamed → stay out of the picture (just the LED + faint cell number). As soon as a
        // vessel name is configured we always show a nameplate, even when healthy, so every cell is labelled.
        if (feed.Health == FeedHealth.Healthy && name.Length == 0)
        {
            DrawAmbient(g, quad, inset, scale, feed, led);
            return;
        }

        DrawPill(g, quad, inset, scale, maxWidth, feed, led, name);
    }

    /// <summary>Derive a badge's top-left draw origin from the active corner (<see cref="_badgeCorner"/>,
    /// the dashboard Position knob). Badge widths are dynamic, so the caller measures its pill first then
    /// anchors: 0=TL (default), 1=TR, 2=BR, 3=BL. Right-anchored when corner ∈ {1,2}; bottom-anchored when
    /// corner ∈ {2,3}.</summary>
    private void AnchorBadge(Rectangle quad, float inset, float w, float h, out float ox, out float oy)
    {
        ox = (_badgeCorner == 1 || _badgeCorner == 2) ? quad.Right - inset - w : quad.X + inset;
        oy = (_badgeCorner == 2 || _badgeCorner == 3) ? quad.Bottom - inset - h : quad.Y + inset;
    }

    /// <summary>Healthy feed: just a glowing LED + a faint cell number, so good video stays unobstructed.
    /// No pill surface, so the Opacity knob doesn't affect it (by design — it is already minimal).</summary>
    private void DrawAmbient(Graphics g, Rectangle quad, float inset, float scale, FeedController feed, Color led)
    {
        float ledD = 13f * scale;
        using var numFont = Theme.SemiBold(9.5f * scale);
        using var nb = new SolidBrush(Color.FromArgb(150, TextPrimary));
        using var fmt = Typographic();
        string n = feed.CellNumber.ToString(CultureInfo.InvariantCulture);
        var ns = g.MeasureString(n, numFont, int.MaxValue, fmt);

        // Anchor by the LED + gap + number extent (the content row); the LED's soft glow may bleed a touch
        // past it, exactly as it does at the default top-left corner.
        AnchorBadge(quad, inset, ledD + 7f * scale + ns.Width, ledD, out float ox, out float oy);

        DrawLed(g, ox, oy, ledD, led);
        g.DrawString(n, numFont, nb, ox + ledD + 7f * scale, oy + (ledD - ns.Height) / 2f, fmt);
    }

    /// <summary>
    /// Frosted-glass nameplate: a brand-blue cell badge, a status LED, the operator-set vessel name, and —
    /// for any non-healthy feed — the status word + live metric. A healthy named feed shows the name only
    /// (the green LED already signals LIVE), keeping the wall calm; trouble adds the status text back.
    /// The vessel name is ellipsized to <paramref name="maxWidth"/> so it can never spill past its quadrant.
    /// </summary>
    private void DrawPill(Graphics g, Rectangle quad, float inset, float scale, float maxWidth, FeedController feed, Color led, string name)
    {
        float pad = 9f * scale;
        float gap = 9f * scale;
        float pillH = 36f * scale;
        float radius = 9f * scale;
        float badge = pillH - 2f * (6f * scale);   // brand number-badge square
        float ledD = 11f * scale;

        using var fmt = Typographic();
        using var numFont = Theme.SemiBold(12f * scale);
        using var nameFont = Theme.SemiBold(11.5f * scale);
        using var statusFont = Theme.SemiBold(9.5f * scale);
        using var metricFont = Theme.Regular(9.5f * scale);

        bool hasName = name.Length > 0;
        bool showStatus = feed.Health != FeedHealth.Healthy;   // calm when healthy: name + green LED only

        string word = StatusWord(feed);
        string metric = MetricText(feed);

        var wordSize = showStatus ? g.MeasureString(word, statusFont, int.MaxValue, fmt) : SizeF.Empty;
        var metricSize = showStatus && !string.IsNullOrEmpty(metric)
            ? g.MeasureString(metric, metricFont, int.MaxValue, fmt)
            : SizeF.Empty;
        float metricGap = metricSize.Width > 0 ? 8f * scale : 0;

        // Reserve all the fixed (non-name) widths first, then hand the vessel name whatever space is left
        // and ellipsize it to fit — so a long name can't push the pill past the quadrant edge.
        float statusW = showStatus ? wordSize.Width + metricGap + metricSize.Width : 0;
        float sepGap = (hasName && showStatus) ? 10f * scale : 0;
        if (hasName)
        {
            float nameMax = maxWidth - (pad + badge + gap + ledD + gap + sepGap + statusW + pad);
            name = Fit(g, name, nameFont, fmt, nameMax);
            hasName = name.Length > 0;
            sepGap = (hasName && showStatus) ? 10f * scale : 0;
        }
        var nameSize = hasName ? g.MeasureString(name, nameFont, int.MaxValue, fmt) : SizeF.Empty;

        float contentW = badge + gap + ledD + gap
                       + (hasName ? nameSize.Width : 0)
                       + sepGap + statusW;
        float pillW = pad + contentW + pad;

        AnchorBadge(quad, inset, pillW, pillH, out float ox, out float oy);
        var pill = new RectangleF(ox, oy, pillW, pillH);

        DrawPillSurface(g, pill, radius, scale);

        float cx = ox + pad;
        float midY = oy + pillH / 2f;

        // Brand number badge.
        var badgeRect = new RectangleF(cx, midY - badge / 2f, badge, badge);
        using (var bpath = Rounded(badgeRect, 5f * scale))
        {
            using (var bg = new LinearGradientBrush(badgeRect, BrandAccentHi, BrandDeep, LinearGradientMode.Vertical))
                g.FillPath(bg, bpath);
            using (var bborder = new Pen(Color.FromArgb(90, 255, 255, 255), 1f))
                g.DrawPath(bborder, bpath);
        }
        using (var numBrush = new SolidBrush(Color.White))
        {
            string n = feed.CellNumber.ToString(CultureInfo.InvariantCulture);
            var ns = g.MeasureString(n, numFont, int.MaxValue, fmt);
            g.DrawString(n, numFont, numBrush, badgeRect.X + (badge - ns.Width) / 2f,
                badgeRect.Y + (badge - ns.Height) / 2f, fmt);
        }
        cx += badge + gap;

        // Status LED.
        DrawLed(g, cx, midY - ledD / 2f, ledD, led);
        cx += ledD + gap;

        // Vessel name (primary, bright).
        if (hasName)
        {
            using var nameBrush = new SolidBrush(TextPrimary);
            g.DrawString(name, nameFont, nameBrush, cx, midY - nameSize.Height / 2f, fmt);
            cx += nameSize.Width + sepGap;
        }

        // Status word (health-tinted) + muted metric — only when the feed isn't healthy.
        if (showStatus)
        {
            using (var wordBrush = new SolidBrush(led))
                g.DrawString(word, statusFont, wordBrush, cx, midY - wordSize.Height / 2f, fmt);
            cx += wordSize.Width + metricGap;
            if (metricSize.Width > 0)
            {
                using var metricBrush = new SolidBrush(TextMuted);
                g.DrawString(metric, metricFont, metricBrush, cx, midY - metricSize.Height / 2f, fmt);
            }
        }
    }

    /// <summary>
    /// "Name only" nameplate: the same frosted-glass pill surface as <see cref="DrawPill"/>, but containing
    /// only a status LED + the vessel name (ellipsized to <paramref name="maxWidth"/>). No cell badge, no
    /// status word, no metric — the operator-friendly "vessel board" look.
    /// </summary>
    private void DrawNamePill(Graphics g, Rectangle quad, float inset, float scale, float maxWidth, Color led, string label)
    {
        float pad = 9f * scale;
        float gap = 9f * scale;
        float pillH = 36f * scale;
        float radius = 9f * scale;
        float ledD = 11f * scale;

        using var fmt = Typographic();
        using var nameFont = Theme.SemiBold(11.5f * scale);

        // Reserve the LED + paddings, then ellipsize the label into whatever's left so it never overflows.
        float labelMax = maxWidth - (pad + ledD + gap + pad);
        label = Fit(g, label, nameFont, fmt, labelMax);
        var labelSize = label.Length > 0 ? g.MeasureString(label, nameFont, int.MaxValue, fmt) : SizeF.Empty;

        float contentW = ledD + (label.Length > 0 ? gap + labelSize.Width : 0);
        float pillW = pad + contentW + pad;

        AnchorBadge(quad, inset, pillW, pillH, out float ox, out float oy);
        var pill = new RectangleF(ox, oy, pillW, pillH);

        DrawPillSurface(g, pill, radius, scale);

        float cx = ox + pad;
        float midY = oy + pillH / 2f;

        DrawLed(g, cx, midY - ledD / 2f, ledD, led);
        cx += ledD + gap;

        if (label.Length > 0)
        {
            using var nameBrush = new SolidBrush(TextPrimary);
            g.DrawString(label, nameFont, nameBrush, cx, midY - labelSize.Height / 2f, fmt);
        }
    }

    /// <summary>Paint the shared frosted-glass pill surface — soft drop shadow, rounded fill, glass sheen and
    /// hairline border. The content (badge/LED/name/status) is drawn on top by the caller. Every surface alpha
    /// is scaled by the dashboard Opacity knob (<see cref="ScaleAlpha"/>) so the whole panel fades coherently;
    /// at the default (77%) the alphas are unchanged.</summary>
    private void DrawPillSurface(Graphics g, RectangleF pill, float radius, float scale)
    {
        DrawShadow(g, pill, radius, scale);

        using var path = Rounded(pill, radius);
        using (var fill = new SolidBrush(Color.FromArgb(ScaleAlpha(PillFill.A), PillFill)))
            g.FillPath(fill, path);

        var sheen = new RectangleF(pill.X, pill.Y, pill.Width, pill.Height * 0.5f);
        using (var glass = new LinearGradientBrush(sheen,
                   Color.FromArgb(ScaleAlpha(36), 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
        {
            using var clip = g.Clip;
            g.SetClip(path, CombineMode.Replace);
            g.FillRectangle(glass, sheen);
            g.Clip = clip;
        }

        using (var border = new Pen(Color.FromArgb(ScaleAlpha(PillBorder.A), PillBorder), 1f))
            g.DrawPath(border, path);
    }

    /// <summary>Scale a surface alpha byte by the badge-opacity factor (<see cref="_badgeOpacity"/> /
    /// <see cref="DefaultBadgeOpacity"/>, = 1.0 at the 77% default), clamped to a valid 0..255 byte. Keeps the
    /// frosted-glass fill/shadow/sheen/border fading in proportion as the Opacity knob moves.</summary>
    private byte ScaleAlpha(int alpha) =>
        (byte)Math.Clamp((int)MathF.Round(alpha * (_badgeOpacity / DefaultBadgeOpacity)), 0, 255);

    /// <summary>An LED dot with a soft outer glow and a small specular highlight.</summary>
    private static void DrawLed(Graphics g, float x, float y, float d, Color color)
    {
        float glow = d * 0.9f;
        var glowRect = new RectangleF(x - glow / 2f, y - glow / 2f, d + glow, d + glow);
        using (var gp = new GraphicsPath())
        {
            gp.AddEllipse(glowRect);
            using var pgb = new PathGradientBrush(gp)
            {
                CenterColor = Color.FromArgb(150, color),
                SurroundColors = new[] { Color.FromArgb(0, color) },
            };
            g.FillEllipse(pgb, glowRect);
        }
        using (var core = new SolidBrush(color))
            g.FillEllipse(core, x, y, d, d);
        using (var ring = new Pen(Color.FromArgb(70, 0, 0, 0), 1f))
            g.DrawEllipse(ring, x, y, d, d);
        using (var hi = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
            g.FillEllipse(hi, x + d * 0.26f, y + d * 0.20f, d * 0.30f, d * 0.30f);
    }

    /// <summary>Layered, anti-aliased soft drop shadow built from a few stacked translucent passes. Its alpha
    /// rides the badge Opacity knob (<see cref="ScaleAlpha"/>) with the rest of the surface.</summary>
    private void DrawShadow(Graphics g, RectangleF pill, float radius, float scale)
    {
        for (int i = 4; i >= 1; i--)
        {
            var r = RectangleF.Inflate(pill, i * scale * 0.6f, i * scale * 0.6f);
            r.Offset(0, 2f * scale);
            using var path = Rounded(r, radius + i * scale * 0.6f);
            using var b = new SolidBrush(Color.FromArgb(ScaleAlpha(16), 0, 0, 0));
            g.FillPath(b, path);
        }
    }

    // ---------------------------------------------------------------- text helpers

    private static string StatusWord(FeedController feed) => feed.State switch
    {
        FeedState.Playing => "LIVE",
        FeedState.Connecting => "CONNECTING",
        FeedState.Stalled => "STALLED",
        FeedState.Reconnecting => "RECONNECTING",
        _ => feed.State.ToString().ToUpperInvariant(),
    };

    private static string MetricText(FeedController feed)
    {
        string s = "";
        if (feed.State == FeedState.Playing && feed.InputBitrateKbps >= 1)
            s = FormatRate(feed.InputBitrateKbps);
        if (feed.LostPictures > 0)
            s = (s.Length > 0 ? s + "  ·  " : "") + "lost " + feed.LostPictures.ToString(CultureInfo.InvariantCulture);
        return s;
    }

    private static string FormatRate(double kbps) => kbps >= 1000
        ? (kbps / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " Mb/s"
        : kbps.ToString("0", CultureInfo.InvariantCulture) + " kb/s";

    private static Color LedColor(FeedHealth health) => health switch
    {
        FeedHealth.Healthy => LedHealthy,
        FeedHealth.Warn => LedWarn,
        FeedHealth.Down => LedDown,
        _ => LedConnecting,
    };

    private static StringFormat Typographic() => new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
    };

    /// <summary>Trim <paramref name="text"/> with a trailing ellipsis until it fits in <paramref name="maxW"/>.
    /// Returns "" when there's no room at all, the original string when it already fits.</summary>
    private static string Fit(Graphics g, string text, Font font, StringFormat fmt, float maxW)
    {
        if (maxW <= 0) return string.Empty;
        if (g.MeasureString(text, font, int.MaxValue, fmt).Width <= maxW) return text;
        const string ell = "…";
        for (int len = text.Length - 1; len > 0; len--)
        {
            string t = text.Substring(0, len).TrimEnd() + ell;
            if (g.MeasureString(t, font, int.MaxValue, fmt).Width <= maxW) return t;
        }
        return string.Empty;
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ---------------------------------------------------------------- layered-window plumbing

    private void PushLayered(Bitmap bmp)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero, oldBitmap = IntPtr.Zero;
        try
        {
            hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memDc, hBitmap);

            var size = new SIZE(bmp.Width, bmp.Height);
            var src = new POINT(0, 0);
            var dst = new POINT(Bounds.Left, Bounds.Top);
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA,
            };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
            if (hBitmap != IntPtr.Zero)
            {
                SelectObject(memDc, oldBitmap);
                DeleteObject(hBitmap);
            }
            DeleteDC(memDc);
        }
    }

    /// <summary>GDI+ stores straight alpha; UpdateLayeredWindow wants premultiplied. Convert in place.</summary>
    private static void Premultiply(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            int bytes = Math.Abs(data.Stride) * bmp.Height;
            byte[] buf = new byte[bytes];
            Marshal.Copy(data.Scan0, buf, 0, bytes);
            for (int i = 0; i < bytes; i += 4)   // BGRA
            {
                byte a = buf[i + 3];
                if (a == 255) continue;
                if (a == 0) { buf[i] = buf[i + 1] = buf[i + 2] = 0; continue; }
                buf[i] = (byte)(buf[i] * a / 255);
                buf[i + 1] = (byte)(buf[i + 1] * a / 255);
                buf[i + 2] = (byte)(buf[i + 2] * a / 255);
            }
            Marshal.Copy(buf, 0, data.Scan0, bytes);
        }
        finally { bmp.UnlockBits(data); }
    }

    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const int ULW_ALPHA = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int Cx, Cy; public SIZE(int cx, int cy) { Cx = cx; Cy = cy; } }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
}
