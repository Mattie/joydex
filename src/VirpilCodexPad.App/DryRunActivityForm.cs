using VirpilCodexPad.Core.Config;

namespace VirpilCodexPad.App;

internal sealed class DryRunActivityForm : Form
{
    private readonly CompanionConfig _config;
    private readonly HashSet<int> _expectedButtons;
    private readonly HashSet<int> _seenButtons = [];
    private readonly Label _statusLabel = new() { AutoSize = true, Text = "Connecting to throttle..." };
    private readonly Label _summaryLabel = new() { AutoSize = true };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill };
    private readonly ListView _activity = new()
    {
        Dock = DockStyle.Fill,
        FullRowSelect = true,
        GridLines = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        View = View.Details,
    };

    public DryRunActivityForm(CompanionConfig config)
    {
        _config = config;
        _expectedButtons = config.Bindings
            .Where(binding => string.Equals(binding.Trigger, "press", StringComparison.OrdinalIgnoreCase))
            .Select(binding => binding.Button)
            .ToHashSet();

        Text = "Test Virpil Codex Pad";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        MinimumSize = new Size(760, 520);
        Size = new Size(920, 640);
        ShowIcon = false;

        _activity.Columns.Add("Time", 90);
        _activity.Columns.Add("Throttle control", 155);
        _activity.Columns.Add("Codex action", 190);
        _activity.Columns.Add("Result", 420);
        BuildLayout();
        UpdateSummary();
    }

    public void Append(string message)
    {
        if (IsDisposed)
        {
            return;
        }

        var isPress = message.StartsWith("INPUT press ", StringComparison.Ordinal);
        var isRelease = message.StartsWith("INPUT release ", StringComparison.Ordinal);
        if ((!isPress && !isRelease)
            || !TryParseButton(message, out var button))
        {
            return;
        }

        var trigger = isRelease ? "release" : "press";
        var binding = _config.Bindings.FirstOrDefault(candidate =>
            candidate.Button == button
            && string.Equals(candidate.Trigger, trigger, StringComparison.OrdinalIgnoreCase));

        if (binding is not null && isPress)
        {
            _seenButtons.Add(button);
        }

        var bindingName = binding?.Name;
        if (isRelease && bindingName?.EndsWith(" release", StringComparison.OrdinalIgnoreCase) == true)
        {
            bindingName = bindingName[..^" release".Length];
        }

        var controlName = binding is null
            ? $"Logical button {button}"
            : $"{bindingName!.Split(" - ", 2, StringSplitOptions.TrimEntries)[0]} (button {button})";
        var action = binding?.Action ?? "unmapped";
        var result = binding is null
            ? isRelease
                ? "Raw release seen."
                : "Raw press seen. This control needs a mapping."
            : isRelease
                ? "Released."
                : "Pressed.";
        var item = new ListViewItem(DateTime.Now.ToString("h:mm:ss tt"));
        item.SubItems.Add(controlName);
        item.SubItems.Add(action);
        item.SubItems.Add(result);
        _activity.Items.Add(item);
        if (_activity.Items.Count > 200)
        {
            _activity.Items.RemoveAt(0);
        }

        item.EnsureVisible();
        UpdateSummary();
    }

    public void SetConnectionStatus(string status)
    {
        if (!IsDisposed)
        {
            _statusLabel.Text = status;
        }
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            RowCount = 7,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Text = "Dry run is ON. Throttle presses and releases appear here without being sent to Codex.",
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(850, 0),
            Padding = new Padding(0, 6, 0, 6),
            Text = "Each row includes the raw logical button number.\r\n1. M2: press B1-B6.   2. M3: press B1-B6.   3. M4: press B1-B6.\r\n4. Press E1, then turn it one detent right and left.   5. Toggle T4 on and off to inspect button 37 press/release.",
        }, 0, 1);
        layout.Controls.Add(_statusLabel, 0, 2);
        layout.Controls.Add(_summaryLabel, 0, 3);
        layout.Controls.Add(_progress, 0, 4);
        layout.Controls.Add(_activity, 0, 5);

        var clear = new Button { AutoSize = true, Text = "Clear results" };
        clear.Click += (_, _) =>
        {
            _activity.Items.Clear();
            _seenButtons.Clear();
            UpdateSummary();
        };
        var close = new Button { AutoSize = true, Text = "Close" };
        close.Click += (_, _) => Close();
        var footer = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 0, 0),
        };
        footer.Controls.Add(close);
        footer.Controls.Add(clear);
        layout.Controls.Add(footer, 0, 6);
        Controls.Add(layout);
    }

    private void UpdateSummary()
    {
        var expected = Math.Max(_expectedButtons.Count, 1);
        _progress.Maximum = expected;
        _progress.Value = Math.Min(_seenButtons.Count, expected);
        _summaryLabel.Text = $"{_seenButtons.Count} of {_expectedButtons.Count} mapped controls seen";
    }

    private static bool TryParseButton(string message, out int button)
    {
        const string marker = "/button ";
        var markerIndex = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            button = 0;
            return false;
        }

        var start = markerIndex + marker.Length;
        var length = 0;
        while (start + length < message.Length && char.IsDigit(message[start + length]))
        {
            length++;
        }

        return int.TryParse(message.AsSpan(start, length), out button);
    }
}
