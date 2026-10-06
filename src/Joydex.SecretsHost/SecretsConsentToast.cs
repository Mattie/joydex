using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Joydex.Secrets;

namespace Joydex.SecretsHost;

internal sealed record SecretsConsentDecision(
    SecretsConsentChoice Choice,
    bool ApplyToAllCommands);

internal sealed class SecretsConsentToast : Form
{
    private const int ToastWidth = 760;
    private const int ToastHeight = 538;
    private const int RequestContentWidth = 350;
    private const int DecisionContentWidth = 312;

    private static readonly Color WindowBorderColor = Color.FromArgb(105, 113, 126);
    private static readonly Color DividerColor = Color.FromArgb(217, 221, 228);
    private static readonly Color RequestBackgroundColor = Color.FromArgb(247, 248, 250);
    private static readonly Color MutedColor = Color.FromArgb(97, 105, 119);
    private static readonly Color TextColor = Color.FromArgb(23, 26, 32);

    private readonly CheckBox _applyToAllCommands;
    private readonly Button _allowOnce;
    private readonly Button _allowFor24Hours;
    private readonly Button _allowAlways;
    private readonly Button _denyThisTime;
    private readonly Button _denyForever;
    private readonly Label _decisionHeading;
    private readonly Label _decisionExplanation;
    private Label _requestState = null!;
    private DateTimeOffset _expiresAt;
    private readonly System.Windows.Forms.Timer _expirationTimer = new() { Interval = 1000 };

    public SecretsConsentToast(SecretsPendingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        AttemptId = request.AttemptId;
        _expiresAt = request.ExpiresAt;
        DetachedReason = request.DetachedReason;

        Text = "Joydex Secrets Manager";
        AccessibleName = "Joydex Secrets approval";
        BackColor = WindowBorderColor;
        ClientSize = new Size(ToastWidth, ToastHeight);
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        Padding = new Padding(1);

        var frame = new TableLayoutPanel
        {
            BackColor = Color.White,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        frame.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        frame.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        frame.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        frame.Controls.Add(BuildTitleBar(), 0, 0);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52.5f));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 1));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 47.5f));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(BuildRequestPanel(request), 0, 0);
        content.Controls.Add(new Panel
        {
            BackColor = DividerColor,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        }, 1, 0);
        content.Controls.Add(BuildDecisionPanel(
            out _decisionHeading,
            out _decisionExplanation,
            out _allowOnce,
            out _allowFor24Hours,
            out _allowAlways,
            out _denyThisTime,
            out _denyForever,
            out _applyToAllCommands), 2, 0);
        frame.Controls.Add(content, 0, 1);
        Controls.Add(frame);

        // The complete 96-DPI layout must exist before WinForms performs its DPI pass.
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;

        _applyToAllCommands.CheckedChanged += (_, _) =>
        {
            UpdateDecisionControls();
        };
        ApplyRequestState(request);
        _expirationTimer.Tick += (_, _) =>
        {
            if (DateTimeOffset.UtcNow < _expiresAt) return;
            TimedOut = true;
            Close();
        };
        Load += (_, _) => PositionAtTaskbar();
        Shown += (_, _) =>
        {
            PositionAtTaskbar();
            NativeMethods.ShowTopmostWithoutActivation(Handle);
            _expirationTimer.Start();
        };
        KeyDown += (_, args) =>
        {
            if (args.KeyCode == Keys.Escape) Close();
        };
        FormClosed += (_, _) =>
        {
            _expirationTimer.Stop();
            _expirationTimer.Dispose();
        };
        SizeChanged += (_, _) => ApplyRoundedRegion();
    }

    public SecretsConsentDecision? Decision { get; private set; }

    public Guid AttemptId { get; }

    public SecretsDetachReason? DetachedReason { get; private set; }

    public bool TimedOut { get; private set; }

    protected override bool ShowWithoutActivation => true;

    public void UpdateRequest(SecretsPendingRequest request)
    {
        if (request.AttemptId != AttemptId) throw new ArgumentException("The request attempt changed.", nameof(request));
        if (request.DetachedReason == DetachedReason && request.ExpiresAt == _expiresAt) return;
        ApplyRequestState(request);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int dropShadow = 0x00020000;
            var parameters = base.CreateParams;
            parameters.ClassStyle |= dropShadow;
            return parameters;
        }
    }

    private Control BuildTitleBar()
    {
        var chrome = new TableLayoutPanel
        {
            BackColor = Color.White,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        chrome.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        chrome.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        chrome.RowStyles.Add(new RowStyle(SizeType.Absolute, 1));

        var titleBar = new Panel
        {
            BackColor = Color.White,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = new Padding(14, 0, 5, 0),
        };
        var title = TextLabel(
            "Joydex Secrets Manager",
            10.5f,
            FontStyle.Regular,
            250,
            Padding.Empty);
        title.Dock = DockStyle.Left;
        title.TextAlign = ContentAlignment.MiddleLeft;
        title.AutoSize = false;
        title.Width = 250;

        var close = new Button
        {
            AccessibleName = "Close",
            BackColor = Color.White,
            Dock = DockStyle.Right,
            FlatStyle = FlatStyle.Flat,
            Font = UiFont(15, FontStyle.Regular),
            ForeColor = Color.FromArgb(76, 81, 91),
            Margin = Padding.Empty,
            Size = new Size(34, 30),
            TabStop = false,
            Text = "×",
            UseVisualStyleBackColor = false,
        };
        close.FlatAppearance.BorderSize = 0;
        close.FlatAppearance.MouseOverBackColor = Color.FromArgb(242, 243, 245);
        close.FlatAppearance.MouseDownBackColor = Color.FromArgb(232, 234, 238);
        close.Click += (_, _) => Close();

        titleBar.Controls.Add(close);
        titleBar.Controls.Add(title);
        AttachWindowDrag(titleBar);
        AttachWindowDrag(title);
        chrome.Controls.Add(titleBar, 0, 0);
        chrome.Controls.Add(new Panel
        {
            BackColor = DividerColor,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
        }, 0, 1);
        return chrome;
    }

    private Control BuildRequestPanel(SecretsPendingRequest request)
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = RequestBackgroundColor,
            Padding = new Padding(24, 24, 24, 20),
            Margin = Padding.Empty,
        };
        panel.Controls.Add(TextLabel(
            "Agent Secret Request",
            15.75f,
            FontStyle.Bold,
            RequestContentWidth,
            new Padding(0, 0, 0, 9)));

        var lead = TextLabel(
            $"{request.ClientLabel} ({request.ProgramLabel}) requests to use "
            + $"{string.Join(", ", request.EnvironmentVariables)}.",
            11.25f,
            FontStyle.Regular,
            RequestContentWidth,
            new Padding(0, 0, 0, 16));
        panel.Controls.Add(lead);

        _requestState = TextLabel(
            string.Empty,
            9.75f,
            FontStyle.Bold,
            RequestContentWidth,
            new Padding(0, 0, 0, 14));
        _requestState.AutoSize = true;
        _requestState.BackColor = Color.FromArgb(229, 238, 255);
        _requestState.ForeColor = Color.FromArgb(26, 79, 158);
        _requestState.Padding = new Padding(9, 6, 9, 6);
        _requestState.Visible = false;
        panel.Controls.Add(_requestState);

        var card = new RoundedPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.White,
            BorderColor = DividerColor,
            BorderRadius = 8,
            Margin = Padding.Empty,
            Padding = new Padding(15, 13, 15, 14),
        };
        var details = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.White,
            ColumnCount = 2,
            RowCount = 10,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            Width = RequestContentWidth - 30,
        };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var detailsHeading = TextLabel(
            "REQUEST DETAILS",
            9.75f,
            FontStyle.Bold,
            RequestContentWidth - 30,
            new Padding(0, 0, 0, 9));
        detailsHeading.ForeColor = MutedColor;
        details.Controls.Add(detailsHeading, 0, 0);
        details.SetColumnSpan(detailsHeading, 2);
        AddDetail(details, 1, "Who", request.ClientLabel);
        AddDetail(details, 2, "What", string.Join(", ", request.EnvironmentVariables), monospace: true);
        AddDetail(details, 3, "Env", "Other caller variables are inherited."
            + (request.Lifetime == SecretsExecutionLifetime.Detached
                ? Environment.NewLine + "Detached: survives caller and Joydex exits. "
                    + (request.DetachedTimeoutSeconds is { } seconds ? $"Stops after {seconds} seconds." : "Runs until stopped.")
                : string.Empty));
        AddDetail(
            details,
            4,
            "Where",
            $"Project  {request.ProjectReference}{Environment.NewLine}Worktree  {request.WorktreeReference}");
        AddDetail(details, 5, "Why", request.Reason);
        AddDetail(details, 6, "Via", request.CommandLine, monospace: true);
        AddDetail(
            details,
            7,
            "Checks",
            request.FingerprintInputs.Count == 0
                ? "None"
                : string.Join(Environment.NewLine, request.FingerprintInputs),
            monospace: request.FingerprintInputs.Count > 0);
        AddDetail(details, 8, "Output", request.OutputDisclosure switch
        {
            SecretOutputDisclosure.None => "Hidden",
            SecretOutputDisclosure.Summary => "Sanitized output",
            SecretOutputDisclosure.Passthrough => "Direct command output",
            _ => request.OutputDisclosure.ToString(),
        });
        var shortDigest = request.OperationDigest.Length <= 12
            ? request.OperationDigest
            : request.OperationDigest[..12];
        AddDetail(
            details,
            9,
            "Match",
            $"SHA-256  {shortDigest}..."
            + (request.CommandLineTruncated
                ? Environment.NewLine + "The command shown above is shortened. This fingerprint covers the full command."
                : string.Empty),
            monospace: !request.CommandLineTruncated);
        details.Location = new Point(card.Padding.Left, card.Padding.Top);
        card.Controls.Add(details);
        panel.Controls.Add(card);
        return panel;
    }

    private Control BuildDecisionPanel(
        out Label decisionHeading,
        out Label decisionExplanation,
        out Button allowOnce,
        out Button allowFor24Hours,
        out Button allowAlways,
        out Button denyThisTime,
        out Button denyForever,
        out CheckBox applyToAllCommands)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(24, 24, 24, 20),
            Margin = Padding.Empty,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        decisionHeading = TextLabel(
            "Approve this request?",
            13.5f,
            FontStyle.Bold,
            DecisionContentWidth,
            new Padding(0, 0, 0, 7));
        panel.Controls.Add(decisionHeading, 0, 0);
        decisionExplanation = TextLabel(
            "Choose how long Joydex should remember your answer.",
            11.25f,
            FontStyle.Regular,
            DecisionContentWidth,
            new Padding(0, 0, 0, 14));
        decisionExplanation.ForeColor = MutedColor;
        panel.Controls.Add(decisionExplanation, 0, 1);

        var allowStack = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.White,
            FlowDirection = FlowDirection.TopDown,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            WrapContents = false,
        };
        allowOnce = ChoiceButton(
            "Allow once",
            "Use the secret for this request only",
            SecretsConsentChoice.Yes,
            primary: true);
        allowStack.Controls.Add(allowOnce);
        allowFor24Hours = ChoiceButton(
            "Allow for 24 hours",
            "Remember this command until tomorrow",
            SecretsConsentChoice.Yes24Hours);
        allowStack.Controls.Add(allowFor24Hours);
        allowAlways = ChoiceButton(
            "Allow always",
            "Remember this command until you revoke it",
            SecretsConsentChoice.YesAlways);
        allowStack.Controls.Add(allowAlways);
        panel.Controls.Add(allowStack, 0, 2);

        applyToAllCommands = new CheckBox
        {
            AutoSize = false,
            Checked = false,
            Font = UiFont(9.75f, FontStyle.Regular),
            ForeColor = Color.FromArgb(85, 94, 107),
            Size = new Size(DecisionContentWidth, 42),
            Text = "Apply my decision to all future commands from this agent",
            AccessibleName = "Apply my decision to all future commands from this agent",
            Margin = new Padding(2, 11, 0, 0),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        panel.Controls.Add(applyToAllCommands, 0, 3);

        var denyRow = new TableLayoutPanel
        {
            BackColor = Color.White,
            ColumnCount = 2,
            RowCount = 1,
            Dock = DockStyle.Fill,
            Height = 42,
            Margin = new Padding(0, 13, 0, 0),
            Padding = Padding.Empty,
        };
        denyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        denyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        denyRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        denyThisTime = ChoiceButton(
            "Deny this time",
            null,
            SecretsConsentChoice.No,
            compact: true);
        denyThisTime.Dock = DockStyle.Fill;
        denyThisTime.Margin = new Padding(0, 0, 4, 0);
        denyRow.Controls.Add(denyThisTime, 0, 0);
        denyForever = ChoiceButton(
            "Deny forever",
            null,
            SecretsConsentChoice.Never,
            danger: true,
            compact: true);
        denyForever.Dock = DockStyle.Fill;
        denyForever.Margin = new Padding(4, 0, 0, 0);
        denyRow.Controls.Add(denyForever, 1, 0);
        panel.Controls.Add(denyRow, 0, 5);
        return panel;
    }

    private void ApplyRequestState(SecretsPendingRequest request)
    {
        DetachedReason = request.DetachedReason;
        _expiresAt = request.ExpiresAt;
        if (request.DetachedReason is { } detachedReason)
        {
            _requestState.Text = detachedReason == SecretsDetachReason.RunWithoutSecrets
                ? "The agent continued without Joydex injecting these secrets."
                : "The agent stopped waiting. This command will not run.";
            _requestState.BackColor = Color.FromArgb(245, 239, 222);
            _requestState.ForeColor = Color.FromArgb(111, 78, 18);
            _requestState.Visible = true;
            _decisionHeading.Text = "Set a rule for next time?";
            _decisionExplanation.Text = "This request can no longer run. Check the box to remember a choice for future commands from this agent.";
            _denyThisTime.Text = "Dismiss";
            _denyThisTime.AccessibleName = "Dismiss";
        }
        else
        {
            _requestState.Text = request.NewAliases.Count switch
            {
                0 => string.Empty,
                1 => $"FIRST USE · {request.NewAliases[0]} is new for this agent and project",
                _ => $"FIRST USE · {request.NewAliases.Count} secrets are new for this agent and project",
            };
            _requestState.BackColor = Color.FromArgb(229, 238, 255);
            _requestState.ForeColor = Color.FromArgb(26, 79, 158);
            _requestState.Visible = request.NewAliases.Count > 0;
            _decisionHeading.Text = "Approve this request?";
            _decisionExplanation.Text = "Choose how long Joydex should remember your answer.";
            _denyThisTime.Text = "Deny this time";
            _denyThisTime.AccessibleName = "Deny this time";
        }
        UpdateDecisionControls();
    }

    private void UpdateDecisionControls()
    {
        var detached = DetachedReason is not null;
        _allowOnce.Visible = !detached;
        _allowOnce.Enabled = !detached && !_applyToAllCommands.Checked;
        _denyThisTime.Enabled = detached || !_applyToAllCommands.Checked;
        _allowFor24Hours.Enabled = !detached || _applyToAllCommands.Checked;
        _allowAlways.Enabled = !detached || _applyToAllCommands.Checked;
        _denyForever.Enabled = !detached || _applyToAllCommands.Checked;
    }

    private Button ChoiceButton(
        string text,
        string? description,
        SecretsConsentChoice choice,
        bool primary = false,
        bool danger = false,
        bool compact = false)
    {
        var button = new ConsentChoiceButton(text, description, primary, danger)
        {
            AccessibleName = text,
            AccessibleDescription = description,
            Margin = compact ? Padding.Empty : new Padding(0, 0, 0, 7),
            Size = compact ? new Size(152, 42) : new Size(DecisionContentWidth, 56),
        };
        button.Click += (_, _) =>
        {
            Decision = new(choice, _applyToAllCommands.Checked);
            Close();
        };
        return button;
    }

    private static Label TextLabel(
        string text,
        float size,
        FontStyle style,
        int width,
        Padding margin) => new()
    {
        AutoSize = true,
        Font = UiFont(size, style),
        ForeColor = TextColor,
        MaximumSize = new Size(width, 0),
        Text = text,
        Margin = margin,
        UseMnemonic = false,
    };

    private static void AddDetail(
        TableLayoutPanel card,
        int row,
        string label,
        string value,
        bool monospace = false)
    {
        var name = TextLabel(label, 9.75f, FontStyle.Regular, 66, new Padding(0, 3, 0, 3));
        name.ForeColor = MutedColor;
        var content = TextLabel(value, 9.75f, FontStyle.Regular, 220, new Padding(0, 3, 0, 3));
        if (monospace) content.Font = new Font("Cascadia Mono", 9.25f, FontStyle.Regular);
        card.Controls.Add(name, 0, row);
        card.Controls.Add(content, 1, row);
    }

    private static Font UiFont(float size, FontStyle style) => new("Segoe UI", size, style);

    private void PositionAtTaskbar()
    {
        var workArea = (Screen.PrimaryScreen ?? Screen.FromHandle(Handle)).WorkingArea;
        var inset = Math.Max(12, (int)Math.Round(18 * DeviceDpi / 96f));
        Location = new Point(
            Math.Max(workArea.Left, workArea.Right - Width - inset),
            Math.Max(workArea.Top, workArea.Bottom - Height - inset));
    }

    private void ApplyRoundedRegion()
    {
        using var path = RoundedPanel.CreateRoundedPath(new Rectangle(0, 0, Width, Height), 12);
        Region?.Dispose();
        Region = new Region(path);
    }

    private void AttachWindowDrag(Control control)
    {
        control.MouseDown += (_, args) =>
        {
            if (args.Button != MouseButtons.Left) return;
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(Handle, 0x00A1, 0x0002, 0);
        };
    }

    private static class NativeMethods
    {
        private static readonly IntPtr HwndTopmost = new(-1);
        private const uint SwpNoSize = 0x0001;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;

        internal static bool ShowTopmostWithoutActivation(IntPtr handle) => SetWindowPos(
            handle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoSize | SwpNoMove | SwpNoActivate | SwpShowWindow);

        [DllImport("user32.dll")]
        internal static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr handle, int message, int wParam, int lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr handle,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);
    }
}

internal sealed class RoundedPanel : Panel
{
    public RoundedPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    public Color BorderColor { get; init; }

    public int BorderRadius { get; init; } = 8;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Color.White);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CreateRoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), BorderRadius);
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = CreateRoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), BorderRadius);
        using var pen = new Pen(BorderColor);
        e.Graphics.DrawPath(pen, path);
    }

    internal static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        var diameter = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class ConsentChoiceButton : Button
{
    private static readonly Color TextColor = Color.FromArgb(23, 26, 32);
    private static readonly Color PrimaryColor = Color.FromArgb(23, 105, 210);
    private static readonly Color DangerColor = Color.FromArgb(165, 32, 32);

    private readonly string _title;
    private readonly string? _description;
    private readonly bool _primary;
    private readonly bool _danger;
    private bool _hovered;
    private bool _pressed;

    public ConsentChoiceButton(string title, string? description, bool primary, bool danger)
    {
        _title = title;
        _description = description;
        _primary = primary;
        _danger = danger;
        Text = title;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint,
            true);
    }

    protected override void OnMouseEnter(EventArgs eventArgs)
    {
        _hovered = true;
        Invalidate();
        base.OnMouseEnter(eventArgs);
    }

    protected override void OnMouseLeave(EventArgs eventArgs)
    {
        _hovered = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(eventArgs);
    }

    protected override void OnMouseDown(MouseEventArgs eventArgs)
    {
        _pressed = eventArgs.Button == MouseButtons.Left;
        Invalidate();
        base.OnMouseDown(eventArgs);
    }

    protected override void OnMouseUp(MouseEventArgs eventArgs)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(eventArgs);
    }

    protected override void OnEnabledChanged(EventArgs eventArgs)
    {
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(eventArgs);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Color.White);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        int Px(int value) => (int)Math.Round(value * DeviceDpi / 96f);
        using var path = RoundedPanel.CreateRoundedPath(bounds, Px(6));

        var background = BackgroundColor();
        var border = BorderColor();
        using (var brush = new SolidBrush(background)) e.Graphics.FillPath(brush, path);
        using (var pen = new Pen(border)) e.Graphics.DrawPath(pen, path);

        var titleColor = !Enabled
            ? Color.FromArgb(149, 155, 165)
            : _primary ? Color.White : _danger ? DangerColor : TextColor;
        using var titleFont = new Font("Segoe UI", 10.5f, FontStyle.Bold);
        if (_description is null)
        {
            TextRenderer.DrawText(
                e.Graphics,
                _title,
                titleFont,
                ClientRectangle,
                titleColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
        else
        {
            var textWidth = Math.Max(0, Width - Px(20));
            TextRenderer.DrawText(
                e.Graphics,
                _title,
                titleFont,
                new Rectangle(Px(10), Px(7), textWidth, Px(20)),
                titleColor,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            using var descriptionFont = new Font("Segoe UI", 9, FontStyle.Regular);
            var descriptionColor = !Enabled
                ? Color.FromArgb(149, 155, 165)
                : _primary ? Color.FromArgb(220, 233, 251) : Color.FromArgb(104, 113, 127);
            TextRenderer.DrawText(
                e.Graphics,
                _description,
                descriptionFont,
                new Rectangle(Px(10), Px(28), textWidth, Px(18)),
                descriptionColor,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        if (Focused && ShowFocusCues)
        {
            var focus = Rectangle.Inflate(ClientRectangle, -4, -4);
            ControlPaint.DrawFocusRectangle(e.Graphics, focus, titleColor, background);
        }
    }

    private Color BackgroundColor()
    {
        if (!Enabled) return Color.FromArgb(238, 240, 243);
        if (_primary)
        {
            if (_pressed) return Color.FromArgb(15, 82, 170);
            return _hovered ? Color.FromArgb(18, 92, 186) : PrimaryColor;
        }

        if (_pressed) return Color.FromArgb(226, 230, 236);
        return _hovered ? Color.FromArgb(237, 241, 247) : Color.White;
    }

    private Color BorderColor()
    {
        if (!Enabled) return Color.FromArgb(214, 218, 224);
        if (_primary) return BackgroundColor();
        return _danger ? Color.FromArgb(217, 169, 169) : Color.FromArgb(184, 190, 200);
    }
}
