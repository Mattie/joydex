using System.Drawing.Drawing2D;
using System.Text;
using Joydex.Windows.Voice;

namespace Joydex.App;

internal sealed class RoomVoiceTranscriptView : UserControl
{
    private readonly BufferedPanel _scroll = new()
    {
        AutoScroll = true,
        Dock = DockStyle.Fill,
        Padding = new Padding(14),
    };
    private readonly BufferedTableLayoutPanel _messages = new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 1,
        Dock = DockStyle.Top,
        GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        Margin = Padding.Empty,
        Padding = Padding.Empty,
    };
    private readonly Label _empty = new()
    {
        AccessibleName = "Empty Room Voice conversation",
        Dock = DockStyle.Fill,
        Text = "No conversation yet.\n\nWake the room device or refresh to load recent speech.",
        TextAlign = ContentAlignment.MiddleCenter,
        Tag = ThemeTone.Subtle,
    };
    private readonly RoundedButton _latestButton = new()
    {
        AutoSize = true,
        Text = "Latest ↓",
        Visible = false,
    };
    private IReadOnlyList<RoomVoiceConversationEntry> _entries = [];
    private readonly List<RenderedEntry> _renderedEntries = [];
    private bool _showRaw;
    private DateOnly? _lastRenderedDate;
    private bool _scrollRestoreScheduled;
    private int _pendingScroll;
    private long _pendingUserScrollVersion;
    private long _userScrollVersion;
    private int _programmaticScrollDepth;
    private bool _followNewest = true;
    private int _lastMessageWidth = -1;

    public RoomVoiceTranscriptView()
    {
        DoubleBuffered = true;
        _messages.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _scroll.Controls.Add(_messages);
        Controls.Add(_scroll);
        Controls.Add(_empty);
        Controls.Add(_latestButton);
        _empty.BringToFront();
        _latestButton.BringToFront();
        _latestButton.Click += (_, _) => FollowNewest();
        _scroll.ClientSizeChanged += (_, _) =>
        {
            UpdateMessageWidth();
            PositionLatestButton();
        };
        _scroll.Scroll += (_, eventArgs) => ObserveScroll(eventArgs);
        ClientSizeChanged += (_, _) => PositionLatestButton();
    }

    public bool Render(IReadOnlyList<RoomVoiceConversationEntry> entries, bool showRaw)
    {
        _entries = entries;
        _empty.Visible = entries.Count == 0;
        var showRawChanged = _showRaw != showRaw;
        _showRaw = showRaw;
        var rebuild = RequiresRebuild(entries);
        var append = entries.Count > _renderedEntries.Count;
        var update = showRawChanged;
        if (!update && !rebuild)
        {
            for (var index = 0; index < _renderedEntries.Count; index++)
            {
                if (_renderedEntries[index].Entry != entries[index])
                {
                    update = true;
                    break;
                }
            }
        }

        if (!rebuild && !append && !update)
        {
            return false;
        }

        var previousScroll = CurrentScrollOffset();

        if (rebuild || append)
        {
            _scroll.SuspendLayout();
            _messages.SuspendLayout();
        }
        try
        {
            if (rebuild)
            {
                Rebuild(entries, showRaw);
            }
            else
            {
                for (var index = 0; index < _renderedEntries.Count; index++)
                {
                    UpdateRenderedEntry(
                        _renderedEntries[index],
                        entries[index],
                        showRaw,
                        showRawChanged);
                }

                for (var index = _renderedEntries.Count; index < entries.Count; index++)
                {
                    AppendEntry(entries[index], showRaw);
                }
            }
        }
        finally
        {
            if (rebuild || append)
            {
                _messages.ResumeLayout(performLayout: true);
                _scroll.ResumeLayout(performLayout: true);
            }
        }

        if (rebuild || _lastMessageWidth < 0)
        {
            UpdateMessageWidth(force: true);
        }
        ScheduleScrollRestore(previousScroll);
        return true;
    }

    private void ScheduleScrollRestore(int previousScroll)
    {
        if (!IsHandleCreated)
        {
            return;
        }
        if (_scrollRestoreScheduled)
        {
            return;
        }
        _pendingScroll = previousScroll;
        _pendingUserScrollVersion = _userScrollVersion;
        _scrollRestoreScheduled = true;
        BeginInvoke(() =>
        {
            _scrollRestoreScheduled = false;
            if (IsDisposed || Disposing)
            {
                return;
            }

            if (_followNewest)
            {
                SetScrollOffset(NewestScrollOffset());
            }
            else if (_pendingUserScrollVersion == _userScrollVersion)
            {
                SetScrollOffset(Math.Min(_pendingScroll, NewestScrollOffset()));
            }
        });
    }

    private void ObserveScroll(ScrollEventArgs eventArgs)
    {
        if (_programmaticScrollDepth != 0 || !IsUserScroll(eventArgs.Type))
        {
            return;
        }

        _userScrollVersion++;
        SetFollowNewest(IsNearNewest(
            eventArgs.NewValue,
            _scroll.VerticalScroll.Minimum,
            _scroll.VerticalScroll.Maximum,
            _scroll.VerticalScroll.LargeChange,
            _scroll.VerticalScroll.Visible));
    }

    private static bool IsUserScroll(ScrollEventType type) => type is
        ScrollEventType.First or
        ScrollEventType.Last or
        ScrollEventType.LargeDecrement or
        ScrollEventType.LargeIncrement or
        ScrollEventType.SmallDecrement or
        ScrollEventType.SmallIncrement or
        ScrollEventType.ThumbPosition or
        ScrollEventType.ThumbTrack;

    internal static bool IsNearNewest(
        int value,
        int minimum,
        int maximum,
        int largeChange,
        bool visible)
    {
        if (!visible)
        {
            return true;
        }

        var newest = Math.Max(minimum, maximum - largeChange + 1);
        return value >= newest - 8;
    }

    internal static int ResolveScrollTarget(
        bool follow,
        int previousScroll,
        int minimum,
        int maximum,
        int largeChange)
    {
        var newest = Math.Max(minimum, maximum - largeChange + 1);
        return follow
            ? newest
            : Math.Clamp(previousScroll, minimum, newest);
    }

    private int CurrentScrollOffset() => Math.Max(0, -_scroll.AutoScrollPosition.Y);

    private int NewestScrollOffset() => Math.Max(
        0,
        _scroll.DisplayRectangle.Height - _scroll.ClientSize.Height);

    private void SetScrollOffset(int offset)
    {
        var target = Math.Clamp(offset, 0, NewestScrollOffset());
        if (CurrentScrollOffset() == target)
        {
            return;
        }

        _programmaticScrollDepth++;
        try
        {
            _scroll.AutoScrollPosition = new Point(0, target);
        }
        finally
        {
            _programmaticScrollDepth--;
        }
    }

    private void FollowNewest()
    {
        SetFollowNewest(true);
        if (!IsHandleCreated)
        {
            return;
        }

        BeginInvoke(() =>
        {
            if (!IsDisposed && !Disposing)
            {
                SetScrollOffset(NewestScrollOffset());
            }
        });
    }

    private void SetFollowNewest(bool follow)
    {
        _followNewest = follow;
        _latestButton.Visible = !follow && _entries.Count > 0;
        if (_latestButton.Visible)
        {
            _latestButton.BringToFront();
        }
    }

    private void PositionLatestButton()
    {
        var size = _latestButton.PreferredSize;
        _latestButton.Size = size;
        _latestButton.Location = new Point(
            Math.Max(0, ClientSize.Width - size.Width - 24),
            Math.Max(0, ClientSize.Height - size.Height - 24));
    }

    internal bool FollowNewestEnabled => _followNewest;

    internal bool LatestButtonVisible => _latestButton.Visible;

    internal void ObserveUserScrollForTest(
        int value,
        int minimum,
        int maximum,
        int largeChange,
        bool visible)
    {
        _userScrollVersion++;
        SetFollowNewest(IsNearNewest(value, minimum, maximum, largeChange, visible));
    }

    internal void FollowNewestForTest() => SetFollowNewest(true);

    private bool RequiresRebuild(IReadOnlyList<RoomVoiceConversationEntry> entries)
    {
        if (entries.Count < _renderedEntries.Count)
        {
            return true;
        }

        for (var index = 0; index < _renderedEntries.Count; index++)
        {
            var previous = _renderedEntries[index].Entry;
            var current = entries[index];
            if (!string.Equals(previous.Id, current.Id, StringComparison.Ordinal)
                || previous.Kind != current.Kind
                || DateOnly.FromDateTime(previous.Timestamp.LocalDateTime)
                    != DateOnly.FromDateTime(current.Timestamp.LocalDateTime))
            {
                return true;
            }
        }

        return false;
    }

    private void Rebuild(IReadOnlyList<RoomVoiceConversationEntry> entries, bool showRaw)
    {
        var oldControls = _messages.Controls.Cast<Control>().ToArray();
        _messages.Controls.Clear();
        foreach (var control in oldControls)
        {
            control.Dispose();
        }
        _messages.RowStyles.Clear();
        _messages.RowCount = 0;
        _renderedEntries.Clear();
        _lastRenderedDate = null;
        foreach (var entry in entries)
        {
            AppendEntry(entry, showRaw);
        }
    }

    private void AppendEntry(RoomVoiceConversationEntry entry, bool showRaw)
    {
        var entryDate = DateOnly.FromDateTime(entry.Timestamp.LocalDateTime);
        if (_lastRenderedDate != entryDate)
        {
            AddRow(CreateDateSeparator(entry.Timestamp.LocalDateTime.Date));
            _lastRenderedDate = entryDate;
        }

        var rendered = CreateMessageRow(entry, showRaw);
        AddRow(rendered.Row);
        _renderedEntries.Add(rendered);
    }

    private static void UpdateRenderedEntry(
        RenderedEntry rendered,
        RoomVoiceConversationEntry entry,
        bool showRaw,
        bool refreshDisplayText)
    {
        if (!refreshDisplayText && rendered.Entry == entry)
        {
            return;
        }

        if (rendered.Bubble is not null)
        {
            rendered.Bubble.SetMessage(
                entry.Kind == CodexVoiceConversationKind.User ? "YOU" : "COMPUTER",
                entry.Timestamp.LocalDateTime.ToString("t"),
                DisplayText(entry, showRaw),
                entry.IsPartial);
        }
        else if (rendered.Activity is not null
                 && !string.Equals(rendered.Activity.Text, entry.Text, StringComparison.Ordinal))
        {
            rendered.Activity.Text = entry.Text;
        }

        rendered.Entry = entry;
    }

    public string BuildPlainText()
    {
        var result = new StringBuilder();
        foreach (var entry in _entries)
        {
            if (result.Length > 0)
            {
                result.AppendLine().AppendLine();
            }
            var role = entry.Kind switch
            {
                CodexVoiceConversationKind.User => "YOU",
                CodexVoiceConversationKind.Assistant => "COMPUTER",
                _ => "ACTIVITY",
            };
            var text = _showRaw && entry.RawText is { Length: > 0 } raw ? raw : entry.Text;
            result.Append(role)
                .Append("  ")
                .Append(entry.Timestamp.LocalDateTime.ToString("t"))
                .AppendLine()
                .Append(text);
        }
        return result.ToString();
    }

    public void ApplyTheme()
    {
        BackColor = JoydexTheme.Surface;
        _scroll.BackColor = JoydexTheme.Surface;
        _messages.BackColor = JoydexTheme.Surface;
        _empty.BackColor = JoydexTheme.Surface;
        _empty.ForeColor = JoydexTheme.TextFaint;
        _empty.Font = JoydexTheme.FontFor(this, JoydexTheme.UiFont);
        ThemeService.Apply(this);
        Invalidate(true);
    }

    private void AddRow(Control control)
    {
        var row = _messages.RowCount++;
        _messages.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _messages.Controls.Add(control, 0, row);
    }

    private Control CreateDateSeparator(DateTime date) => new Label
    {
        Anchor = AnchorStyles.None,
        AutoSize = true,
        Font = JoydexTheme.FontFor(this, JoydexTheme.SectionFont),
        ForeColor = JoydexTheme.TextFaint,
        Margin = new Padding(0, 8, 0, 14),
        Tag = ThemeTone.Faint,
        Text = date.Date == DateTime.Today ? "TODAY" : date.ToString("D"),
        TextAlign = ContentAlignment.MiddleCenter,
    };

    private RenderedEntry CreateMessageRow(RoomVoiceConversationEntry entry, bool showRaw)
    {
        var row = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 3,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 12),
            RowCount = 1,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 15));

        if (entry.Kind == CodexVoiceConversationKind.Activity)
        {
            var activity = new Label
            {
                Anchor = AnchorStyles.None,
                AutoSize = true,
                BackColor = JoydexTheme.TagBg,
                Font = JoydexTheme.FontFor(this, JoydexTheme.SectionFont),
                ForeColor = JoydexTheme.TagText,
                Margin = new Padding(8, 3, 8, 3),
                Padding = new Padding(10, 5, 10, 5),
                Tag = ThemeTone.Subtle,
                Text = entry.Text,
                TextAlign = ContentAlignment.MiddleCenter,
            };
            row.Controls.Add(activity, 0, 0);
            row.SetColumnSpan(activity, 3);
            return new RenderedEntry(entry, row, Bubble: null, Activity: activity);
        }

        var bubble = new RoomVoiceBubblePanel(entry.Kind)
        {
            Anchor = entry.Kind == CodexVoiceConversationKind.User
                ? AnchorStyles.Top | AnchorStyles.Right
                : AnchorStyles.Top | AnchorStyles.Left,
            Margin = Padding.Empty,
        };
        bubble.SetMessage(
            entry.Kind == CodexVoiceConversationKind.User ? "YOU" : "COMPUTER",
            entry.Timestamp.LocalDateTime.ToString("t"),
            DisplayText(entry, showRaw),
            entry.IsPartial);
        if (_lastMessageWidth > 0)
        {
            bubble.SetMaximumTextWidth(_lastMessageWidth);
        }

        var column = entry.Kind == CodexVoiceConversationKind.User ? 1 : 0;
        row.Controls.Add(bubble, column, 0);
        row.SetColumnSpan(bubble, 2);
        return new RenderedEntry(entry, row, bubble, Activity: null);
    }

    private static string DisplayText(RoomVoiceConversationEntry entry, bool showRaw) =>
        showRaw && entry.RawText is { Length: > 0 } raw ? raw : entry.Text;

    private void UpdateMessageWidth(bool force = false)
    {
        var width = Math.Max(1, _scroll.ClientSize.Width - _scroll.Padding.Horizontal - 20);
        if (_messages.Width != width)
        {
            _messages.Width = width;
        }
        var bubbleWidth = Math.Max(240, (int)(width * 0.68));
        if (!force && _lastMessageWidth == bubbleWidth)
        {
            return;
        }
        _lastMessageWidth = bubbleWidth;
        foreach (var rendered in _renderedEntries)
        {
            if (rendered.Bubble is not null)
            {
                rendered.Bubble.SetMaximumTextWidth(bubbleWidth);
            }
        }
    }

    internal IReadOnlyList<Control> RenderedMessageRows =>
        _renderedEntries.Select(entry => entry.Row).ToArray();

    internal IReadOnlyList<string> RenderedMessageTexts =>
        _renderedEntries
            .Where(entry => entry.Bubble is not null)
            .Select(entry => entry.Bubble!.MessageText)
            .ToArray();

    internal IReadOnlyList<TextBox> RenderedMessageTextControls =>
        _renderedEntries
            .Where(entry => entry.Bubble is not null)
            .Select(entry => entry.Bubble!.MessageTextControl)
            .ToArray();

    private sealed class RenderedEntry(
        RoomVoiceConversationEntry entry,
        Control row,
        RoomVoiceBubblePanel? Bubble,
        Label? Activity)
    {
        public RoomVoiceConversationEntry Entry { get; set; } = entry;
        public Control Row { get; } = row;
        public RoomVoiceBubblePanel? Bubble { get; } = Bubble;
        public Label? Activity { get; } = Activity;
    }

    private sealed class BufferedPanel : Panel
    {
        public BufferedPanel() => SetStyle(
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint,
            value: true);
    }

    private sealed class BufferedTableLayoutPanel : TableLayoutPanel
    {
        public BufferedTableLayoutPanel() => SetStyle(
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint,
            value: true);
    }
}

internal sealed class RoomVoiceBubblePanel : Panel
{
    private readonly CodexVoiceConversationKind _kind;
    private readonly Label _meta = new() { AutoSize = true };
    private readonly TextBox _body = new()
    {
        AccessibleName = "Conversation message",
        BorderStyle = BorderStyle.None,
        Cursor = Cursors.IBeam,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.None,
        ShortcutsEnabled = true,
        TabStop = false,
        WordWrap = true,
    };
    private int _textWidth = -1;

    public RoomVoiceBubblePanel(CodexVoiceConversationKind kind)
    {
        _kind = kind;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        DoubleBuffered = true;
        Padding = new Padding(14, 11, 14, 12);

        var content = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            RowCount = 2,
        };
        content.Controls.Add(_meta, 0, 0);
        content.Controls.Add(_body, 0, 1);
        Controls.Add(content);
        ApplyTheme();
    }

    public Color FillColor { get; private set; }

    internal string MessageText => _body.Text;

    internal TextBox MessageTextControl => _body;

    public void SetMessage(string role, string time, string text, bool partial)
    {
        var metadata = $"{role}  {time}{(partial ? "  · listening…" : string.Empty)}";
        if (!string.Equals(_meta.Text, metadata, StringComparison.Ordinal))
        {
            _meta.Text = metadata;
        }
        if (!string.Equals(_body.Text, text, StringComparison.Ordinal))
        {
            var selectionStart = _body.SelectionStart;
            var selectionLength = _body.SelectionLength;
            _body.Text = text;
            _body.Select(
                Math.Min(selectionStart, text.Length),
                Math.Min(selectionLength, Math.Max(0, text.Length - selectionStart)));
            UpdateMessageBodySize();
        }
    }

    public void SetMaximumTextWidth(int width)
    {
        var textWidth = Math.Max(160, width - Padding.Horizontal);
        if (_textWidth == textWidth && MaximumSize.Width == width)
        {
            return;
        }
        _textWidth = textWidth;
        _meta.MaximumSize = new Size(textWidth, 0);
        MaximumSize = new Size(width, 0);
        UpdateMessageBodySize();
    }

    public void ApplyTheme()
    {
        FillColor = _kind == CodexVoiceConversationKind.User
            ? JoydexTheme.AccentTint
            : JoydexTheme.GroupBg;
        BackColor = Parent?.BackColor ?? JoydexTheme.Surface;
        _meta.BackColor = FillColor;
        _meta.ForeColor = JoydexTheme.AccentText;
        _meta.Font = JoydexTheme.FontFor(this, JoydexTheme.SectionFont);
        _body.BackColor = FillColor;
        _body.ForeColor = JoydexTheme.Text;
        _body.Font = JoydexTheme.FontFor(this, JoydexTheme.UiFont);
        UpdateMessageBodySize();
        Invalidate();
    }

    private void UpdateMessageBodySize()
    {
        if (_textWidth <= 0)
        {
            return;
        }

        var text = _body.Text.Length > 0 ? _body.Text : " ";
        var measured = TextRenderer.MeasureText(
            text,
            _body.Font,
            new Size(_textWidth, int.MaxValue),
            TextFormatFlags.TextBoxControl | TextFormatFlags.WordBreak);
        _body.Size = new Size(
            _textWidth,
            Math.Max(_body.Font.Height + 4, measured.Height + 2));
    }

    protected override void OnPaintBackground(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.Clear(Parent?.BackColor ?? JoydexTheme.Surface);
        eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRectangle(ClientRectangle, JoydexTheme.ScaleLogical(12, DeviceDpi));
        using var brush = new SolidBrush(FillColor);
        eventArgs.Graphics.FillPath(brush, path);
    }

    private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return path;
        }
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}
