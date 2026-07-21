using Joydex.Core.Config;

namespace Joydex.App;

internal sealed class DryRunActivityForm : ThemedForm
{
    private readonly CompanionConfig _config;
    private readonly HashSet<int> _expectedButtons;
    private readonly HashSet<int> _seenButtons = [];
    private readonly Label _statusLabel = new() { AutoSize = true, Text = "Connecting to throttle..." };
    private readonly Label _summaryLabel = new() { AutoSize = true };
    private readonly ProgressBar _progress = new()
    {
        Dock = DockStyle.Fill,
        AccessibleName = "Mapped controls seen",
    };
    private readonly ModernDataGridView _activity = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AutoGenerateColumns = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        MultiSelect = false,
        ReadOnly = true,
        AccessibleName = "Dry-run activity",
    };

    public DryRunActivityForm(CompanionConfig config)
    {
        _config = config;
        _expectedButtons = config.Bindings
            .Where(binding => string.Equals(binding.Trigger, "press", StringComparison.OrdinalIgnoreCase))
            .Select(binding => binding.Button)
            .ToHashSet();

        Text = "Test Joydex";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(760, 520);
        Size = new Size(920, 640);
        ShowIcon = false;

        _activity.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Time",
            Name = "Time",
            FillWeight = 90,
        });
        _activity.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Control",
            Name = "Control",
            FillWeight = 160,
        });
        _activity.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Codex action",
            Name = "Action",
            FillWeight = 150,
        });
        _activity.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Result",
            Name = "Result",
            FillWeight = 240,
        });
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
        var rowIndex = _activity.Rows.Add(
            DateTime.Now.ToString("HH:mm:ss"),
            controlName,
            action,
            result);
        if (_activity.Rows.Count > 200)
        {
            _activity.Rows.RemoveAt(0);
            rowIndex--;
        }

        if (rowIndex >= 0 && _activity.IsHandleCreated)
        {
            ScrollToNewestActivity();
        }

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
        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            RowCount = 3,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var summaryCard = new CardPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 12),
        };
        var summaryLayout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 5,
        };
        summaryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        summaryLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        summaryLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        summaryLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        summaryLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        summaryLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        summaryLayout.Controls.Add(new Label
        {
            AutoSize = true,
            Font = JoydexTheme.UiSemiboldFont,
            Text = "Dry run is ON. Throttle presses and releases appear here without being sent to Codex.",
        }, 0, 0);
        summaryLayout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(850, 0),
            Padding = new Padding(0, 6, 0, 6),
            Text = "Each row includes the raw logical button number.\r\n1. M2: press B1-B6.   2. M3: press B1-B6.   3. M4: press B1-B6.\r\n4. Press E1, then turn it one detent right and left.   5. Toggle T4 on and off to inspect button 37 press/release.",
        }, 0, 1);
        summaryLayout.Controls.Add(_statusLabel, 0, 2);
        summaryLayout.Controls.Add(_summaryLabel, 0, 3);
        summaryLayout.Controls.Add(_progress, 0, 4);
        summaryCard.Controls.Add(summaryLayout);
        root.Controls.Add(summaryCard, 0, 0);

        var activityCard = new CardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
        };
        var activityLayout = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 2,
        };
        activityLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        activityLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        activityLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        activityLayout.Controls.Add(new Label
        {
            AutoSize = true,
            Font = JoydexTheme.SectionFont,
            Margin = new Padding(0, 0, 0, 10),
            Text = "RECENT ACTIVITY",
        }, 0, 0);
        activityLayout.Controls.Add(_activity, 0, 1);
        activityCard.Controls.Add(activityLayout);
        root.Controls.Add(activityCard, 0, 1);

        var clear = new RoundedButton { Text = "Clear results" };
        clear.Click += (_, _) =>
        {
            _activity.Rows.Clear();
            _seenButtons.Clear();
            UpdateSummary();
        };
        var close = new RoundedButton
        {
            Text = "Close",
            Variant = ButtonVariant.Primary,
        };
        close.Click += (_, _) => Close();
        CancelButton = close;
        var footer = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 3,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 10, 0, 0),
            RowCount = 1,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(clear, 1, 0);
        footer.Controls.Add(close, 2, 0);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);
    }

    private void UpdateSummary()
    {
        var expected = Math.Max(_expectedButtons.Count, 1);
        _progress.Maximum = expected;
        _progress.Value = Math.Min(_seenButtons.Count, expected);
        _summaryLabel.Text = $"{_seenButtons.Count} of {_expectedButtons.Count} mapped controls seen";
    }

    private void ScrollToNewestActivity()
    {
        if (_activity.Rows.Count > 0
            && _activity.ClientSize.Height > _activity.ColumnHeadersHeight
            && _activity.DisplayedRowCount(includePartialRow: true) > 0)
        {
            _activity.FirstDisplayedScrollingRowIndex = _activity.Rows.Count - 1;
        }
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
