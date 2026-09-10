using System.Diagnostics;
using Apud.Data;

namespace Apud.App;

/// <summary>
/// The modal progress dialog shown while a big pushed import runs on a background thread
/// (see <see cref="MainForm.CommitImportWithProgress"/>). Plain WinForms — a standard
/// <see cref="ProgressBar"/> and a few labels, in Apud's ordinary style — but it still
/// shows the exact numbers as they land: the running record count and percentage, the
/// BIB/AUT split, and a live rate + estimate. When the last record is in it flips to
/// "Writing to disk…" (the WAL checkpoint folding in) before the caller closes it.
///
/// <see cref="Report"/> is called on the UI thread (MainForm marshals through
/// <see cref="Progress{T}"/>), and the import fires it every 100 records, so it just
/// updates the controls directly — no animation, no custom painting.
/// </summary>
public sealed class ImportProgressForm : Form
{
    private readonly int _total;
    private readonly Stopwatch _clock = new();
    private double _ratePerSec;

    private readonly ProgressBar _bar;
    private readonly Label _status;
    private readonly Label _count;
    private readonly Label _split;
    private readonly Label _rate;

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
        ClientSize = new Size(400, 132);
        Font = new Font("Segoe UI", 9f);

        int pad = 12;
        int w = ClientSize.Width - pad * 2;

        _status = new Label { Location = new Point(pad, pad), AutoSize = true, Text = "Importing catalogue…" };
        _bar = new ProgressBar
        {
            Location = new Point(pad, 34),
            Size = new Size(w, 22),
            Minimum = 0,
            Maximum = _total,
            Value = 0,
        };
        _count = new Label
        {
            Location = new Point(pad, 64),
            AutoSize = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Text = $"0 of {_total:N0} records (0%)",
        };
        _split = new Label { Location = new Point(pad, 86), AutoSize = true, ForeColor = SystemColors.GrayText, Text = "BIB 0   ·   AUT 0" };
        _rate = new Label { Location = new Point(pad, 106), AutoSize = true, ForeColor = SystemColors.GrayText, Text = "" };

        Controls.Add(_status);
        Controls.Add(_bar);
        Controls.Add(_count);
        Controls.Add(_split);
        Controls.Add(_rate);

        _clock.Start();
    }

    /// <summary>Feed the dialog a fresh snapshot from the running commit. UI-thread only,
    /// so it updates the controls straight away.</summary>
    public void Report(ImportProgress p)
    {
        _bar.Value = Math.Min(p.Done, _bar.Maximum);

        int pct = (int)Math.Round(Math.Min(p.Done / (double)_total, 1.0) * 100);
        _count.Text = $"{p.Done:N0} of {_total:N0} records ({pct}%)";
        _split.Text = $"BIB {p.Bib:N0}   ·   AUT {p.Aut:N0}";

        // Live throughput, smoothed a little so the estimate doesn't jitter.
        double secs = _clock.Elapsed.TotalSeconds;
        if (secs > 0.05 && p.Done > 0)
        {
            double inst = p.Done / secs;
            _ratePerSec = _ratePerSec <= 0 ? inst : (_ratePerSec * 0.7 + inst * 0.3);
        }

        if (p.Done >= _total)
        {
            _status.Text = "Writing to disk…";
            _rate.Text = "Finishing up — folding the write-ahead log back in…";
        }
        else
        {
            _rate.Text = ThroughputLine(p.Done);
        }
    }

    /// <summary>"22,400 records/sec · about 3s left" — the live rate and a rounded estimate,
    /// or a gentler message before there's enough data to be honest about the rate.</summary>
    private string ThroughputLine(int done)
    {
        if (_ratePerSec <= 1 || done <= 0) return "Measuring speed…";
        int remaining = Math.Max(_total - done, 0);
        double etaSec = remaining / _ratePerSec;
        string eta = etaSec < 1 ? "almost done"
            : etaSec < 60 ? $"about {Math.Ceiling(etaSec):N0}s left"
            : $"about {Math.Ceiling(etaSec / 60):N0} min left";
        return $"{_ratePerSec:N0} records/sec · {eta}";
    }
}
