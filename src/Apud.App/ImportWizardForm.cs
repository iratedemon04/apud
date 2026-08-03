using Apud.Data;
using Marc.Core.Mrk;

namespace Apud.App;

/// <summary>
/// The import report wizard (Module 5c): shows the whole Analyze result before
/// anything touches the catalogue — per-file grid, line-numbered diagnostics for
/// the selected file, run-level errors (duplicate 001s) up top — and makes the
/// PUSHED/DRAFTS choice explicit. Import stays disabled while the chosen mode is
/// illegal. Cancel/close commits nothing (the 5a engine is all-or-nothing anyway;
/// this dialog only ever hands back a mode).
/// </summary>
public sealed class ImportWizardForm : Form
{
    private readonly ImportReport _report;
    private readonly ListView _files;
    private readonly TextBox _detail;
    private readonly RadioButton _asPushed;
    private readonly RadioButton _asDrafts;
    private readonly Button _import;

    public ImportMode SelectedMode { get; private set; } = ImportMode.AsDrafts;

    public ImportWizardForm(string folder, ImportReport report)
    {
        _report = report;

        Text = "Import Records";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Size = new Size(760, 560);
        MinimumSize = new Size(640, 480);

        int warnings = report.Files.Sum(f => f.Diagnostics.Count(d => d.Severity == MrkSeverity.Warning));
        int errors = report.Files.Sum(f => f.Diagnostics.Count(d => d.Severity == MrkSeverity.Error));

        var summary = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(8, 4, 8, 0),
            Text = $"{folder}\r\n" +
                   $"{report.Files.Count} file(s) — {report.TotalRecords} record(s), " +
                   $"{warnings} warning(s), {errors} error(s), {report.RunErrors.Count} duplicate-001 problem(s).",
        };

        Control? runErrors = null;
        if (report.RunErrors.Count > 0)
        {
            runErrors = new TextBox
            {
                Dock = DockStyle.Top,
                Height = 70,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                ForeColor = Color.DarkRed,
                Font = new Font("Consolas", 9f),
                Text = string.Join("\r\n", report.RunErrors),
            };
        }

        _files = new ListView
        {
            Dock = DockStyle.Top,
            Height = 180,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
        };
        _files.Columns.Add("File", 320);
        _files.Columns.Add("Records", 70, HorizontalAlignment.Right);
        _files.Columns.Add("Warnings", 70, HorizontalAlignment.Right);
        _files.Columns.Add("Errors", 70, HorizontalAlignment.Right);
        foreach (var f in report.Files)
        {
            int w = f.Diagnostics.Count(d => d.Severity == MrkSeverity.Warning);
            int e = f.Diagnostics.Count(d => d.Severity == MrkSeverity.Error);
            var item = new ListViewItem(Path.GetFileName(f.FilePath));
            item.SubItems.Add(f.RecordCount.ToString());
            item.SubItems.Add(w.ToString());
            item.SubItems.Add(e.ToString());
            if (e > 0) item.ForeColor = Color.DarkRed;
            else if (w > 0) item.ForeColor = Color.DarkGoldenrod;
            item.Tag = f;
            _files.Items.Add(item);
        }
        _files.SelectedIndexChanged += (_, _) => ShowFileDetail();

        _detail = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 9f),
            Text = "Select a file to see its diagnostics.",
        };

        // ----- footer: mode choice + buttons -----
        _asPushed = new RadioButton
        {
            Text = "Import as PUSHED — trusted migration; records keep their 001s and enter search immediately. Requires a run with no problems at all.",
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 22,
        };
        _asDrafts = new RadioButton
        {
            Text = "Import as DRAFTS — records stay out of search until pushed; parse problems tolerated. Duplicate 001s always block the run.",
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 22,
            Checked = true, // default to the safe, non-committal mode (user request 2026-08-02)
        };
        _asPushed.CheckedChanged += (_, _) => UpdateImportEnabled();
        _asDrafts.CheckedChanged += (_, _) => UpdateImportEnabled();

        _import = new Button { Text = "&Import", DialogResult = DialogResult.OK, Width = 90 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        _import.Click += (_, _) =>
            SelectedMode = _asPushed.Checked ? ImportMode.AsPushed : ImportMode.AsDrafts;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 36,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(4),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_import);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 88, Padding = new Padding(8, 4, 8, 0) };
        footer.Controls.Add(_asDrafts);
        footer.Controls.Add(_asPushed);

        Controls.Add(_detail);
        Controls.Add(_files);
        if (runErrors != null) Controls.Add(runErrors);
        Controls.Add(summary);
        Controls.Add(footer);
        Controls.Add(buttons);

        AcceptButton = _import;
        CancelButton = cancel;

        // Default to DRAFTS (above); fall back to PUSHED only if drafts can't
        // commit but pushed can (rare — duplicate 001s block both).
        if (!report.CanCommitAsDrafts && report.CanCommitAsPushed) _asPushed.Checked = true;
        UpdateImportEnabled();
    }

    private void UpdateImportEnabled() =>
        _import.Enabled = _asPushed.Checked ? _report.CanCommitAsPushed : _report.CanCommitAsDrafts;

    private void ShowFileDetail()
    {
        if (_files.SelectedItems.Count == 0) return;
        var f = (ImportFileReport)_files.SelectedItems[0].Tag!;
        _detail.Text = f.Diagnostics.Count == 0
            ? $"{Path.GetFileName(f.FilePath)}: {f.RecordCount} record(s), no problems."
            : string.Join("\r\n", f.Diagnostics.Select(d => d.ToString()));
    }
}
