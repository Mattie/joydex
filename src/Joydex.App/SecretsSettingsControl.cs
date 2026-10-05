using Joydex.Secrets;

namespace Joydex.App;

internal sealed class SecretsSettingsControl : UserControl
{
    private readonly string _policyPath;
    private readonly string _auditPath;
    private readonly string _configurationPath;
    private readonly string _operatingModePath;
    private readonly string _metricsPath;
    private readonly bool _allowChanges;
    private readonly ModernDataGridView _decisions = CreateGrid("SecretsRememberedDecisions");
    private readonly ModernDataGridView _sources = CreateGrid("SecretsSources");
    private readonly ModernDataGridView _activity = CreateGrid("SecretsRecentActivity");
    private readonly StatusLabel _summary = new()
    {
        AutoSize = true,
        Name = "SecretsSettingsSummary",
        Tag = ThemeTone.Subtle,
        Text = "Loading Secrets state...",
    };
    private readonly StatusLabel _operatingModeStatus = new()
    {
        AutoSize = true,
        Font = new Font("Segoe UI", 9F, FontStyle.Bold),
        Name = "SecretsOperatingModeStatus",
        Text = "Loading approval mode...",
    };
    private readonly Label _metricsStatus = new()
    {
        AutoSize = true,
        Name = "SecretsMetricsStatus",
        Padding = new Padding(0, 6, 0, 2),
        Text = "Loading usage...",
    };
    private readonly RoundedButton _autoAllowAll = new()
    {
        AutoSize = true,
        Text = "⚠ Auto-allow all for 24 hours…",
    };
    private readonly RoundedButton _denyAll = new()
    {
        AutoSize = true,
        Text = "Deny all until further notice",
    };
    private readonly RoundedButton _askNormally = new()
    {
        AutoSize = true,
        Text = "Use normal approvals",
    };
    private readonly RoundedButton _revokeDecision = new()
    {
        AutoSize = true,
        Enabled = false,
        Name = "SecretsRevokeDecision",
        Text = "Revoke selected decision",
    };
    private readonly RoundedButton _addSource = new() { AutoSize = true, Text = "Add source" };
    private readonly RoundedButton _editSource = new() { AutoSize = true, Enabled = false, Text = "Edit source" };
    private readonly RoundedButton _removeSource = new() { AutoSize = true, Enabled = false, Text = "Remove source" };
    private readonly RoundedButton _clearActivity = new() { AutoSize = true, Text = "Clear activity" };
    private bool _stateReadable;
    private bool _operatingModeReadable;
    private SecretsOperatingMode? _currentOperatingMode;

    public SecretsSettingsControl(string dataRoot, bool allowChanges)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _policyPath = SecretsPaths.GetPolicyPath(dataRoot);
        _auditPath = SecretsPaths.GetAuditPath(dataRoot);
        _configurationPath = SecretsPaths.GetConfigurationPath(dataRoot);
        _operatingModePath = SecretsPaths.GetOperatingModePath(dataRoot);
        _metricsPath = SecretsPaths.GetMetricsPath(dataRoot);
        _allowChanges = allowChanges;

        AutoScroll = true;
        Dock = DockStyle.Fill;
        Padding = new Padding(12);
        ConfigureColumns();

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            Dock = DockStyle.Top,
            Padding = new Padding(0, 0, 8, 16),
            RowCount = 6,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(Header(), 0, 0);
        layout.Controls.Add(_summary, 0, 1);
        layout.Controls.Add(OperatingModeSection(), 0, 2);
        layout.Controls.Add(DecisionSection(), 0, 3);
        layout.Controls.Add(SourceSection(), 0, 4);
        layout.Controls.Add(ActivitySection(), 0, 5);
        Controls.Add(layout);

        _decisions.SelectionChanged += (_, _) => UpdateActions();
        _sources.SelectionChanged += (_, _) => UpdateActions();
        _revokeDecision.Click += (_, _) => RevokeSelectedDecision();
        _addSource.Click += (_, _) => EditSource(create: true);
        _editSource.Click += (_, _) => EditSource(create: false);
        _removeSource.Click += (_, _) => RemoveSource();
        _clearActivity.Click += (_, _) => ClearActivity();
        _autoAllowAll.Click += (_, _) => AutoAllowAll();
        _denyAll.Click += (_, _) => SetOperatingMode(
            store => store.DenyAll(DateTimeOffset.UtcNow));
        _askNormally.Click += (_, _) => SetOperatingMode(
            store => store.Ask(DateTimeOffset.UtcNow));
        Load += (_, _) => Reload();
    }

    private Control OperatingModeSection()
    {
        var group = new GroupBox
        {
            Dock = DockStyle.Top,
            Height = 190,
            Margin = new Padding(0, 12, 0, 0),
            Padding = new Padding(10),
            Text = "Approval mode and usage",
        };
        var copy = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };
        copy.Controls.Add(_operatingModeStatus);
        copy.Controls.Add(_metricsStatus);
        var commands = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 8, 0, 0),
        };
        commands.Controls.Add(_autoAllowAll);
        commands.Controls.Add(_denyAll);
        commands.Controls.Add(_askNormally);
        group.Controls.Add(copy);
        group.Controls.Add(commands);
        return group;
    }

    private Control Header()
    {
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 8),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var copy = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = Padding.Empty,
        };
        copy.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Text = "Secrets",
        });
        copy.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(760, 0),
            Padding = new Padding(0, 4, 0, 4),
            Text = "Choose where secrets come from, review remembered decisions, and inspect sanitized activity.",
        });
        var refresh = new RoundedButton
        {
            AutoSize = true,
            Name = "SecretsRefresh",
            Text = "Refresh",
        };
        refresh.Click += (_, _) => Reload();
        panel.Controls.Add(copy, 0, 0);
        panel.Controls.Add(refresh, 1, 0);
        return panel;
    }

    private Control DecisionSection()
    {
        var group = new GroupBox
        {
            Dock = DockStyle.Top,
            Height = 270,
            Margin = new Padding(0, 12, 0, 0),
            Padding = new Padding(10),
            Text = "Remembered decisions",
        };
        var commands = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 8, 0, 0),
        };
        commands.Controls.Add(_revokeDecision);
        group.Controls.Add(_decisions);
        group.Controls.Add(commands);
        return group;
    }

    private Control SourceSection()
    {
        var section = ManagedSection(
            "Secret sources",
            _sources,
            [_addSource, _editSource, _removeSource]);
        section.Margin = new Padding(0, 12, 0, 0);
        return section;
    }

    private Control ActivitySection()
    {
        var group = new GroupBox
        {
            Dock = DockStyle.Top,
            Height = 240,
            Margin = new Padding(0, 12, 0, 0),
            Padding = new Padding(10),
            Text = "Recent activity",
        };
        var commands = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 8, 0, 0),
        };
        commands.Controls.Add(_clearActivity);
        group.Controls.Add(_activity);
        group.Controls.Add(commands);
        return group;
    }

    private static Control ManagedSection(
        string title,
        Control grid,
        IReadOnlyList<Control> actions)
    {
        var group = new GroupBox
        {
            Dock = DockStyle.Fill,
            Height = 250,
            Margin = new Padding(0, 0, 8, 0),
            Padding = new Padding(10),
            Text = title,
        };
        var commands = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(0, 8, 0, 0),
        };
        foreach (var action in actions) commands.Controls.Add(action);
        group.Controls.Add(grid);
        group.Controls.Add(commands);
        return group;
    }

    private void ConfigureColumns()
    {
        AddColumn(_decisions, "Decision", 72);
        AddColumn(_decisions, "Agent", 120);
        AddColumn(_decisions, "Project", 90);
        AddColumn(_decisions, "Secrets", 120);
        AddColumn(_decisions, "Scope", 120);
        AddColumn(_decisions, "Expires", 110);
        AddColumn(_sources, "Source", 100);
        AddColumn(_sources, "Variables", 70);
        AddColumn(_sources, "File", 70);
        AddColumn(_sources, "Path", 180);
        AddColumn(_activity, "When", 120);
        AddColumn(_activity, "Agent", 120);
        AddColumn(_activity, "Project", 100);
        AddColumn(_activity, "Event", 120);
        AddColumn(_activity, "Result", 100);
    }

    private void Reload()
    {
        _stateReadable = false;
        _operatingModeReadable = false;
        _currentOperatingMode = null;
        _decisions.Rows.Clear();
        _sources.Rows.Clear();
        _activity.Rows.Clear();
        var now = DateTimeOffset.UtcNow;
        try
        {
            var operatingMode = new SecretsOperatingModeStore(_operatingModePath).Read(now);
            _currentOperatingMode = operatingMode.Mode;
            _operatingModeReadable = true;
            _operatingModeStatus.Text = SecretsOperatingModeUi.Status(operatingMode);
        }
        catch (Exception exception)
        {
            _operatingModeStatus.Text = "Approval mode unavailable: " + exception.Message;
        }

        try
        {
            var metrics = new SecretsMetricsStore(_metricsPath).Read(now);
            _metricsStatus.Text = SecretsOperatingModeUi.Metrics(metrics);
        }
        catch (Exception exception)
        {
            _metricsStatus.Text = "Usage unavailable: " + exception.Message;
        }

        try
        {
            var audit = new SecretsAuditJournal(_auditPath).ReadAll();
            var policy = new SecretsPolicyStore(_policyPath).Read(now);
            var metadata = audit
                .Where(record => record.RuleId is not null)
                .GroupBy(record => record.RuleId!.Value)
                .ToDictionary(group => group.Key, group => group.Last());

            foreach (var rule in policy.Rules.OrderByDescending(rule => rule.IssuedAt))
            {
                metadata.TryGetValue(rule.RuleId, out var record);
                var index = _decisions.Rows.Add(
                    rule.Allow ? "Allow" : "Deny",
                    rule.ClientLabel ?? rule.ClientReference ?? record?.ClientReference ?? "Unknown agent",
                    rule.ProjectReference ?? record?.ProjectReference ?? "Unknown project",
                    rule.Aliases is { Count: > 0 }
                        ? string.Join(", ", rule.Aliases)
                        : record is null
                            ? "Unavailable"
                            : string.Join(", ", record.Aliases),
                    rule.ScopeKind == RememberedGrantScopeKind.ExactOperation
                        ? "This command"
                        : "All commands from agent",
                    rule.ExpiresAt is null
                        ? "Until revoked"
                        : rule.ExpiresAt.Value.ToLocalTime().ToString("g"));
                _decisions.Rows[index].Tag = rule.RuleId;
            }

            var configuration = new SecretsConfigurationStore(_configurationPath).Read();
            foreach (var source in configuration.Sources)
            {
                var index = _sources.Rows.Add(
                    source.DisplayName,
                    source.Aliases.Count,
                    File.Exists(source.FilePath) ? "Found" : "Missing",
                    source.FilePath);
                _sources.Rows[index].Tag = source.SourceId;
            }

            foreach (var record in audit.OrderByDescending(record => record.Timestamp).Take(100))
            {
                _activity.Rows.Add(
                    record.Timestamp.ToLocalTime().ToString("g"),
                    record.ClientReference,
                    record.ProjectReference,
                    Humanize(record.Kind),
                    string.IsNullOrWhiteSpace(record.Outcome) ? "—" : record.Outcome);
            }

            _summary.Text = policy.Rules.Count == 0
                && configuration.Sources.Count == 0
                && audit.Count == 0
                ? "Secrets is ready. Add a source to make environment variables available to agent requests."
                : $"{policy.Rules.Count} remembered decision{Plural(policy.Rules.Count)}, "
                    + $"{configuration.Sources.Count} source{Plural(configuration.Sources.Count)}, "
                    + $"{audit.Count} sanitized activity record{Plural(audit.Count)}.";
            _stateReadable = true;
        }
        catch (Exception exception)
        {
            _summary.Text = "Secrets state could not be read: " + exception.Message;
            _summary.Tag = ThemeTone.Subtle;
        }
        UpdateActions();
    }

    private void AutoAllowAll()
    {
        if (!_allowChanges) return;
        var now = DateTimeOffset.UtcNow;
        if (!SecretsOperatingModeUi.ConfirmAutoAllow(this, now)) return;
        SetOperatingMode(store => store.AutoAllowFor24Hours(now));
    }

    private void SetOperatingMode(
        Func<SecretsOperatingModeStore, SecretsOperatingModeSnapshot> change)
    {
        if (!_allowChanges) return;
        try
        {
            _ = change(new SecretsOperatingModeStore(_operatingModePath));
            Reload();
        }
        catch (Exception exception)
        {
            ShowError("The Secrets approval mode could not be changed", exception);
        }
    }

    private void RevokeSelectedDecision()
    {
        if (!_allowChanges || SelectedTag(_decisions) is not Guid ruleId) return;
        if (MessageBox.Show(
                this,
                "Revoke this remembered decision? The next matching request will ask again.",
                "Revoke remembered decision",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.OK)
        {
            return;
        }
        try
        {
            _ = new SecretsPolicyStore(_policyPath).Revoke(ruleId);
            Reload();
        }
        catch (Exception exception)
        {
            _summary.Text = "The remembered decision could not be revoked: " + exception.Message;
            _summary.Tag = ThemeTone.Subtle;
        }
    }

    private void EditSource(bool create)
    {
        if (!_allowChanges) return;
        try
        {
            var store = new SecretsConfigurationStore(_configurationPath);
            var current = create
                ? null
                : store.Read().Sources.SingleOrDefault(source =>
                    SelectedTag(_sources) is Guid selected && source.SourceId == selected);
            if (!create && current is null) return;
            using var dialog = new SecretsEnvSourceEditorDialog(current);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            store.UpsertEnvSource(
                current?.SourceId,
                dialog.DisplayName,
                dialog.FilePath,
                dialog.AliasKeys);
            Reload();
        }
        catch (Exception exception)
        {
            ShowError("The secret source could not be saved", exception);
        }
    }

    private void RemoveSource()
    {
        if (!_allowChanges || SelectedTag(_sources) is not Guid sourceId) return;
        if (MessageBox.Show(
                this,
                "Remove this secret source? New requests cannot use its aliases until another source is configured.",
                "Remove secret source",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.OK)
        {
            return;
        }
        try
        {
            new SecretsConfigurationStore(_configurationPath).RemoveEnvSource(sourceId);
            Reload();
        }
        catch (Exception exception)
        {
            ShowError("The secret source could not be removed", exception);
        }
    }

    private void ShowError(string action, Exception exception)
    {
        _summary.Text = action + ": " + exception.Message;
        _summary.Tag = ThemeTone.Subtle;
    }

    private void ClearActivity()
    {
        if (!_allowChanges) return;
        if (MessageBox.Show(
                this,
                "Clear the visible Secrets activity log? Remembered decisions and duplicate-launch protection stay in place.",
                "Clear Secrets activity",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.OK)
        {
            return;
        }
        try
        {
            new SecretsAuditJournal(_auditPath).ClearActivity();
            Reload();
        }
        catch (Exception exception)
        {
            ShowError("Secrets activity could not be cleared", exception);
        }
    }

    private void UpdateActions()
    {
        var enabled = _allowChanges && _stateReadable;
        var modeEnabled = _allowChanges && _operatingModeReadable;
        _autoAllowAll.Enabled = modeEnabled;
        _denyAll.Enabled = modeEnabled && _currentOperatingMode != SecretsOperatingMode.DenyAll;
        _askNormally.Enabled = modeEnabled && _currentOperatingMode != SecretsOperatingMode.Ask;
        _revokeDecision.Enabled = enabled && SelectedTag(_decisions) is Guid;
        _addSource.Enabled = enabled && _sources.Rows.Count < SecretsConfigurationStore.MaximumSources;
        _editSource.Enabled = enabled && SelectedTag(_sources) is Guid;
        _removeSource.Enabled = enabled && SelectedTag(_sources) is Guid;
        _clearActivity.Enabled = enabled && _activity.Rows.Count > 0;
    }

    private static object? SelectedTag(DataGridView grid) =>
        grid.SelectedRows.Count == 1 ? grid.SelectedRows[0].Tag : null;

    private static ModernDataGridView CreateGrid(string name) => new()
    {
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AutoGenerateColumns = false,
        BackgroundColor = SystemColors.Window,
        BorderStyle = BorderStyle.None,
        Dock = DockStyle.Fill,
        MultiSelect = false,
        Name = name,
        ReadOnly = true,
        RowHeadersVisible = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
    };

    private static void AddColumn(DataGridView grid, string title, float weight)
    {
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = weight,
            HeaderText = title,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });
    }

    private static string Humanize(SecretsAuditEventKind kind) => kind switch
    {
        SecretsAuditEventKind.RequestReceived => "Request received",
        SecretsAuditEventKind.RequestOutcome => "Request decided",
        SecretsAuditEventKind.RequestDetached => "Agent stopped waiting",
        SecretsAuditEventKind.ConsentDecision => "Consent saved",
        SecretsAuditEventKind.LaunchCommitted => "Launch approved",
        SecretsAuditEventKind.LaunchStarted => "Program started",
        SecretsAuditEventKind.LaunchCompleted => "Program completed",
        SecretsAuditEventKind.LaunchTerminated => "Program stopped",
        SecretsAuditEventKind.LaunchUnconfirmed => "Launch unconfirmed",
        SecretsAuditEventKind.FailedBeforeLaunch => "Launch stopped",
        _ => kind.ToString(),
    };

    private static string Plural(int count) => count == 1 ? string.Empty : "s";
}
