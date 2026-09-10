using System.Diagnostics;
using System.Drawing.Drawing2D;
using Apud.Data;

namespace Apud.App;

/// <summary>
/// The modal progress dialog shown while a big pushed import runs on a background thread
/// (see <see cref="MainForm.CommitImportWithProgress"/>). It is deliberately detailed rather
/// than a bare bar: a large live percentage, an owner-drawn bar that <em>glides</em> to each
/// new value instead of jumping, the exact running record count, the BIB/AUT split as it lands,
/// and a live throughput + estimate. When the last record is in it flips to a brief
/// "Writing to disk…" state (that is the WAL checkpoint folding in) before the caller closes it.
///
/// Everything is painted by hand on a double-buffered surface so it stays smooth; a ~60fps
/// timer eases the shown value toward the real one, which is what makes the motion satisfying.
/// <see cref="Report"/> is called on the UI thread (MainForm marshals through
/// <see cref="Progress{T}"/>), so it only stores state and lets the timer animate.
/// </summary>
public sealed class ImportProgressForm : Form
{
    // Palette — clean, light, ILS-neutral. Fill is a blue gradient; it turns green at the finish.
    private static readonly Color Ink = Color.FromArgb(32, 33, 36);
    private static readonly Color Muted = Color.FromArgb(95, 99, 104);
    private static readonly Color Track = Color.FromArgb(232, 234, 237);
    private static readonly Color FillA = Color.FromArgb(59, 130, 246);   // blue-500
    private static readonly Color FillB = Color.FromArgb(37, 99, 235);    // blue-600
    private static readonly Color DoneA = Color.FromArgb(34, 197, 94);    // green-500
    private static readonly Color DoneB = Color.FromArgb(22, 163, 74);    // green-600

    private readonly int _total;
    private readonly Stopwatch _clock = new();
    private readonly System.Windows.Forms.Timer _anim;

    // Real state, set by Report; the animation eases toward it.
    private int _done;
    private int _bib;
    private int _aut;
    private bool _finalizing;

    // Animated (shown) values.
    private double _shownDone;
    private double _lastRatePerSec;

    public ImportProgressForm(int total)
    {
        _total = Math.Max(total, 1);

        Text = "Importing…";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ControlBox = false;            // no close/minimize: the task ends it, not the user
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(460, 208);
        BackColor = Color.White;
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 9f);

        _anim = new System.Windows.Forms.Timer { Interval = 16 }; // ~60fps
        _anim.Tick += (_, _) => Animate();
        _anim.Start();
        _clock.Start();
    }

    /// <summary>Feed the dialog a fresh snapshot from the running commit. UI-thread only.</summary>
    public void Report(ImportProgress p)
    {
        _done = p.Done;
        _bib = p.Bib;
        _aut = p.Aut;

        // Live throughput off the real numbers (not the eased ones), smoothed a little so the
        // estimate doesn't jitter.
        double secs = _clock.Elapsed.TotalSeconds;
        if (secs > 0.05 && _done > 0)
        {
            double inst = _done / secs;
            _lastRatePerSec = _lastRatePerSec <= 0 ? inst : (_lastRatePerSec * 0.7 + inst * 0.3);
        }

        if (_done >= _total) _finalizing = true;
    }

    /// <summary>Eases the shown value toward the real one each frame and repaints. When they
    /// meet there's nothing to animate, so it idles cheaply.</summary>
    private void Animate()
    {
        double target = _done;
        double delta = target - _shownDone;
        if (Math.Abs(delta) < 0.5)
        {
            if (_shownDone != target) { _shownDone = target; Invalidate(); }
            return;
        }
        _shownDone += delta * 0.18;      // exponential ease-out — quick then settles
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        int pad = 24;
        int w = ClientSize.Width;

        double frac = Math.Min(_shownDone / _total, 1.0);
        int shownDone = (int)Math.Round(_shownDone);
        // Scale the split to the eased count so the two halves always sum to what's shown.
        int denom = Math.Max(_done, 1);
        int shownBib = (int)Math.Round(_bib * (_shownDone / denom));
        int shownAut = Math.Max(shownDone - shownBib, 0);
        bool done = _finalizing && frac >= 0.999;

        // ----- header -----
        using (var head = new Font("Segoe UI", 9.5f, FontStyle.Bold))
            TextRenderer.DrawText(g, "IMPORTING CATALOGUE", head, new Point(pad, 18), Muted);

        // ----- big live percentage -----
        int pct = (int)Math.Round(frac * 100);
        using (var big = new Font("Segoe UI Semibold", 34f, FontStyle.Bold))
        {
            string s = $"{pct}%";
            TextRenderer.DrawText(g, s, big, new Point(pad - 4, 36), done ? DoneB : Ink,
                TextFormatFlags.NoPadding);
        }

        // Right-aligned status word next to the number.
        using (var tag = new Font("Segoe UI", 10f, FontStyle.Regular))
        {
            string word = done ? "Writing to disk…" : "importing";
            Size sz = TextRenderer.MeasureText(g, word, tag);
            TextRenderer.DrawText(g, word, tag,
                new Point(w - pad - sz.Width, 62), done ? DoneB : Muted);
        }

        // ----- the bar -----
        var barRect = new Rectangle(pad, 108, w - pad * 2, 22);
        DrawRoundedFill(g, barRect, Track, null);
        if (frac > 0)
        {
            int fw = Math.Max((int)Math.Round(barRect.Width * frac), barRect.Height);
            var fillRect = new Rectangle(barRect.X, barRect.Y, fw, barRect.Height);
            using var brush = new LinearGradientBrush(
                fillRect, done ? DoneA : FillA, done ? DoneB : FillB, LinearGradientMode.Horizontal);
            DrawRoundedFill(g, fillRect, null, brush);
        }

        // ----- detail lines -----
        int ty = 144;
        using (var strong = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold))
        {
            string count = $"{shownDone:N0} of {_total:N0} records";
            TextRenderer.DrawText(g, count, strong, new Point(pad, ty), Ink);
        }

        // BIB / AUT split, right-aligned on the same row.
        using (var split = new Font("Segoe UI", 10f, FontStyle.Regular))
        {
            string s = $"BIB {shownBib:N0}   ·   AUT {shownAut:N0}";
            Size sz = TextRenderer.MeasureText(g, s, split);
            TextRenderer.DrawText(g, s, split, new Point(w - pad - sz.Width, ty + 1), Muted);
        }

        // Throughput + estimate.
        using (var small = new Font("Segoe UI", 9f, FontStyle.Regular))
        {
            string line = done
                ? "Finishing up — folding the write-ahead log back in…"
                : ThroughputLine();
            TextRenderer.DrawText(g, line, small, new Point(pad, ty + 26), Muted);
        }
    }

    /// <summary>"22,400 records/sec · about 3s left" — the live rate and a rounded estimate,
    /// or a gentler message before there's enough data to be honest about the rate.</summary>
    private string ThroughputLine()
    {
        if (_lastRatePerSec <= 1 || _done <= 0) return "Measuring speed…";
        int remaining = Math.Max(_total - _done, 0);
        double etaSec = remaining / _lastRatePerSec;
        string eta = etaSec < 1 ? "almost done"
            : etaSec < 60 ? $"about {Math.Ceiling(etaSec):N0}s left"
            : $"about {Math.Ceiling(etaSec / 60):N0} min left";
        return $"{_lastRatePerSec:N0} records/sec · {eta}";
    }

    /// <summary>Fills a rounded rectangle with either a solid colour or a brush (one is null).</summary>
    private static void DrawRoundedFill(Graphics g, Rectangle r, Color? solid, Brush? brush)
    {
        int radius = r.Height;
        using var path = new GraphicsPath();
        int d = Math.Min(radius, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        path.CloseFigure();
        if (brush is not null) g.FillPath(brush, path);
        else using (var b = new SolidBrush(solid!.Value)) g.FillPath(b, path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _anim.Dispose();
        base.Dispose(disposing);
    }
}
