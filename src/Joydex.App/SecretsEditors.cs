using Joydex.Secrets;

namespace Joydex.App;

internal sealed class SecretsEnvSourceEditorDialog : Form
{
    private readonly TextBox _displayName = new();
    private readonly TextBox _filePath = new();
    private readonly TextBox _aliases = EditorLayout.Multiline(150);
    private readonly Button _importNames = new() { AutoSize = true, Text = "Import variable names" };

    public SecretsEnvSourceEditorDialog(SecretsEnvSource? current)
    {
        Text = current is null ? "Add secret source" : "Edit secret source";
        _displayName.Text = current?.DisplayName ?? ".env";
        _filePath.Text = current?.FilePath ?? string.Empty;
        _aliases.Text = current is null
            ? string.Empty
            : string.Join(
                Environment.NewLine,
                current.Aliases.Select(alias => string.Equals(alias.Alias, alias.Key, StringComparison.Ordinal)
                    ? alias.Key
                    : $"{alias.Alias}={alias.Key}"));
        _importNames.Click += (_, _) => ImportNames(showErrors: true);
        var names = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            RowCount = 2,
        };
        names.RowStyles.Add(new RowStyle(SizeType.Absolute, _aliases.Height));
        names.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _aliases.Dock = DockStyle.Fill;
        _importNames.Margin = new Padding(0, 8, 0, 0);
        names.Controls.Add(_aliases, 0, 0);
        names.Controls.Add(_importNames, 0, 1);

        var fields = EditorLayout.Create(150,
            ("Source name", _displayName, "A name for this source in Joydex."),
            (".env file", EditorLayout.PathPicker(_filePath, PickFile), "The Secrets broker reads this exact file when it lists aliases or starts an approved command."),
            ("Variables", names, "One imported name per line. Use public-name=ENV_NAME only when you want a different public name."));
        fields.AutoScroll = true;
        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            RowCount = 2,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(fields, 0, 0);
        layout.Controls.Add(EditorLayout.Buttons(this, Save), 0, 1);
        Controls.Add(layout);
        EditorLayout.ConfigureDialog(this);
        ClientSize = new Size(760, 650);
        MinimumSize = new Size(700, 600);
    }

    public string DisplayName => _displayName.Text.Trim();

    public string FilePath => _filePath.Text.Trim();

    public IReadOnlyDictionary<string, string> AliasKeys => ParseVariableMappings(_aliases.Text);

    private void PickFile()
    {
        using var picker = new OpenFileDialog
        {
            CheckFileExists = true,
            FileName = Path.GetFileName(_filePath.Text),
            Filter = "Environment files (.env*)|.env*|All files (*.*)|*.*",
            InitialDirectory = Path.GetDirectoryName(_filePath.Text),
            Title = "Choose the exact .env file Joydex may read",
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        _filePath.Text = picker.FileName;
        ImportNames(showErrors: true);
    }

    private bool ImportNames(bool showErrors)
    {
        try
        {
            if (!File.Exists(FilePath)) throw new ArgumentException("Choose an existing .env file.");
            var keys = ExactEnvSecretProvider.DiscoverKeys(FilePath);
            if (keys.Count == 0) throw new ArgumentException("This .env file has no variable names to import.");
            if (keys.Count > 256) throw new ArgumentException("Joydex can import up to 256 variables from one .env file.");

            IReadOnlyDictionary<string, string> current;
            try
            {
                current = ParseVariableMappings(_aliases.Text);
            }
            catch (ArgumentException)
            {
                current = new Dictionary<string, string>(StringComparer.Ordinal);
            }
            var existingNames = current
                .GroupBy(mapping => mapping.Value, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal);
            _aliases.Text = string.Join(
                Environment.NewLine,
                keys.Select(key => existingNames.TryGetValue(key, out var name)
                    && !string.Equals(name, key, StringComparison.Ordinal)
                        ? $"{name}={key}"
                        : key));
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException)
        {
            if (showErrors) EditorLayout.Invalid(this, exception.Message);
            return false;
        }
    }

    private static IReadOnlyDictionary<string, string> ParseVariableMappings(string text)
    {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in EditorLayout.Lines(text))
        {
            var split = line.IndexOf('=');
            var name = split < 0 ? line : line[..split].Trim();
            var key = split < 0 ? line : line[(split + 1)..].Trim();
            if (name.Length == 0 || key.Length == 0 || !parsed.TryAdd(name, key))
            {
                throw new ArgumentException($"Variable name '{name}' is empty or repeated.");
            }
        }
        return parsed;
    }

    private void Save()
    {
        try
        {
            if (DisplayName.Length is < 1 or > 96) throw new ArgumentException("Enter a source name.");
            if (!File.Exists(FilePath)) throw new ArgumentException("Choose an existing .env file.");
            if (AliasKeys.Count == 0 && !ImportNames(showErrors: false))
            {
                throw new ArgumentException("Import at least one variable name from the .env file.");
            }
            DialogResult = DialogResult.OK;
        }
        catch (ArgumentException exception)
        {
            EditorLayout.Invalid(this, exception.Message);
        }
    }
}

internal sealed class SecretsRecipeEditorDialog : Form
{
    private readonly IReadOnlySet<string> _availableAliases;
    private readonly TextBox _recipeId = new();
    private readonly TextBox _displayName = new();
    private readonly ComboBox _project = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _executable = new();
    private readonly TextBox _workingDirectory = new();
    private readonly TextBox _arguments = EditorLayout.Multiline(78);
    private readonly TextBox _parameters = EditorLayout.Multiline(64);
    private readonly TextBox _secrets = EditorLayout.Multiline(64);
    private readonly TextBox _fingerprints = EditorLayout.Multiline(55);
    private readonly ComboBox _output = new() { DropDownStyle = ComboBoxStyle.DropDownList };

    public SecretsRecipeEditorDialog(
        SecretsRecipe? current,
        IReadOnlyList<string> projects,
        IReadOnlyList<string> aliases)
    {
        _availableAliases = aliases.ToHashSet(StringComparer.Ordinal);
        Text = current is null ? "Add recipe" : "Edit recipe";
        ClientSize = new Size(720, 770);
        MinimumSize = new Size(650, 690);
        _project.Items.AddRange(projects.Cast<object>().ToArray());
        _output.Items.AddRange(Enum.GetNames<SecretOutputDisclosure>());
        _recipeId.Text = current?.RecipeId ?? string.Empty;
        _recipeId.ReadOnly = current is not null;
        _displayName.Text = current?.DisplayName ?? string.Empty;
        _project.SelectedItem = current?.ProjectReference ?? projects.FirstOrDefault();
        _executable.Text = current?.Executable ?? string.Empty;
        _workingDirectory.Text = current?.WorkingDirectory ?? string.Empty;
        _arguments.Text = current is null ? string.Empty : string.Join(Environment.NewLine, current.ArgumentTemplates);
        _parameters.Text = current is null ? string.Empty : string.Join(Environment.NewLine,
            current.Parameters.Select(pair => pair.Key + "=" + string.Join('|', pair.Value)));
        _secrets.Text = current is null ? string.Empty : string.Join(Environment.NewLine,
            current.SecretEnvironment.Select(pair => pair.Key + "=" + pair.Value));
        _fingerprints.Text = current is null ? string.Empty : string.Join(Environment.NewLine, current.FingerprintInputs);
        _output.SelectedItem = (current?.OutputDisclosure ?? SecretOutputDisclosure.None).ToString();

        var fields = EditorLayout.Create(
            ("Recipe ID", _recipeId, "A stable name used by the command-line helper, such as publish-preview."),
            ("Recipe name", _displayName, "The name shown in Joydex."),
            ("Project", _project, "The project allowed to run this recipe."),
            ("Program", EditorLayout.PathPicker(_executable, PickExecutable), "The exact executable Joydex will start."),
            ("Working folder", EditorLayout.PathPicker(_workingDirectory, PickWorkingDirectory), "Must be inside the project folder."),
            ("Arguments", _arguments, "One argument per line. Use {name} for an allowed parameter."),
            ("Parameters", _parameters, "Optional. One allowlist per line: environment=dev|preview"),
            ("Secrets", _secrets, "One environment variable and public alias per line: API_TOKEN=api-token"),
            ("Fingerprint files", _fingerprints, "Optional. One project file per line. Changes require a new approval."),
            ("Command output", _output, "None hides output. Summary returns bounded, redacted output."));
        fields.AutoScroll = true;
        Controls.Add(fields);
        Controls.Add(EditorLayout.Buttons(this, Save));
        EditorLayout.ConfigureDialog(this);
    }

    public SecretsRecipeDraft Draft { get; private set; } = null!;

    private void PickExecutable()
    {
        using var picker = new OpenFileDialog
        {
            CheckFileExists = true,
            FileName = Path.GetFileName(_executable.Text),
            InitialDirectory = Path.GetDirectoryName(_executable.Text),
            Title = "Choose the program this recipe may run",
        };
        if (picker.ShowDialog(this) == DialogResult.OK) _executable.Text = picker.FileName;
    }

    private void PickWorkingDirectory()
    {
        using var picker = new FolderBrowserDialog
        {
            Description = "Choose the recipe working folder",
            InitialDirectory = Directory.Exists(_workingDirectory.Text) ? _workingDirectory.Text : string.Empty,
            ShowNewFolderButton = false,
        };
        if (picker.ShowDialog(this) == DialogResult.OK) _workingDirectory.Text = picker.SelectedPath;
    }

    private void Save()
    {
        try
        {
            var recipeId = _recipeId.Text.Trim();
            var displayName = _displayName.Text.Trim();
            var project = _project.SelectedItem?.ToString() ?? string.Empty;
            if (recipeId.Length is < 1 or > 64
                || recipeId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            {
                throw new ArgumentException("Recipe ID may contain letters, numbers, hyphens, and underscores.");
            }
            if (displayName.Length is < 1 or > 96) throw new ArgumentException("Enter a recipe name.");
            if (string.IsNullOrWhiteSpace(project)) throw new ArgumentException("Choose a project.");
            if (!File.Exists(_executable.Text.Trim())) throw new ArgumentException("Choose an existing program.");
            if (!Directory.Exists(_workingDirectory.Text.Trim())) throw new ArgumentException("Choose an existing working folder.");

            var parameters = ParseParameters(_parameters.Text);
            var secretEnvironment = EditorLayout.ParseMappings(_secrets.Text, "secret mapping");
            if (secretEnvironment.Count == 0) throw new ArgumentException("Add at least one secret mapping.");
            if (secretEnvironment.Values.Any(alias => !_availableAliases.Contains(alias)))
            {
                throw new ArgumentException("Every secret mapping must use an alias from the configured source.");
            }
            Draft = new SecretsRecipeDraft(
                recipeId,
                displayName,
                project,
                Path.GetFullPath(_executable.Text.Trim()),
                EditorLayout.Lines(_arguments.Text),
                Path.GetFullPath(_workingDirectory.Text.Trim()),
                parameters,
                secretEnvironment,
                EditorLayout.Lines(_fingerprints.Text).Select(Path.GetFullPath).ToArray(),
                Enum.Parse<SecretOutputDisclosure>(_output.SelectedItem?.ToString() ?? nameof(SecretOutputDisclosure.None)));
            DialogResult = DialogResult.OK;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            EditorLayout.Invalid(this, exception.Message);
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseParameters(string text)
    {
        var parsed = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var line in EditorLayout.Lines(text))
        {
            var split = line.IndexOf('=');
            if (split <= 0 || split == line.Length - 1)
            {
                throw new ArgumentException("Each parameter must use name=value1|value2.");
            }
            var name = line[..split].Trim();
            var values = line[(split + 1)..].Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (values.Length == 0 || !parsed.TryAdd(name, values))
            {
                throw new ArgumentException($"Parameter '{name}' is empty or repeated.");
            }
        }
        return parsed;
    }
}

internal static class EditorLayout
{
    public static TableLayoutPanel Create(params (string Label, Control Field, string Help)[] rows)
        => Create(125, rows);

    public static TableLayoutPanel Create(
        int labelWidth,
        params (string Label, Control Field, string Help)[] rows)
    {
        if (labelWidth < 1) throw new ArgumentOutOfRangeException(nameof(labelWidth));
        var panel = new TableLayoutPanel
        {
            AutoSize = true,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 16, 18, 8),
            RowCount = rows.Length * 2,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var row = 0;
        foreach (var item in rows)
        {
            item.Field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            item.Field.Margin = new Padding(0, 3, 0, 0);
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Margin = new Padding(0, 7, 10, 0),
                Text = item.Label,
            }, 0, row);
            panel.Controls.Add(item.Field, 1, row++);
            panel.Controls.Add(new Label
            {
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 2, 0, 6),
                MaximumSize = new Size(520, 0),
                Text = item.Help,
            }, 1, row++);
        }
        return panel;
    }

    public static Control PathPicker(TextBox textBox, Action browse)
    {
        var panel = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Fill, Margin = Padding.Empty };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var button = new Button { AutoSize = true, Text = "Browse…", Margin = new Padding(6, 0, 0, 0) };
        button.Click += (_, _) => browse();
        panel.Controls.Add(textBox, 0, 0);
        panel.Controls.Add(button, 1, 0);
        return panel;
    }

    public static FlowLayoutPanel Buttons(Form owner, Action save)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 8, 12, 12),
        };
        var cancel = new Button { AutoSize = true, DialogResult = DialogResult.Cancel, Text = "Cancel" };
        var accept = new Button { AutoSize = true, Text = "Save" };
        accept.Click += (_, _) => save();
        owner.AcceptButton = accept;
        owner.CancelButton = cancel;
        panel.Controls.Add(cancel);
        panel.Controls.Add(accept);
        return panel;
    }

    public static TextBox Multiline(int height) => new()
    {
        AcceptsReturn = true,
        Font = new Font("Cascadia Mono", 9),
        Height = height,
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
    };

    public static IReadOnlyDictionary<string, string> ParseMappings(string text, string label)
    {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Lines(text))
        {
            var split = line.IndexOf('=');
            if (split <= 0 || split == line.Length - 1)
            {
                throw new ArgumentException($"Each {label} must use name=value.");
            }
            var name = line[..split].Trim();
            var value = line[(split + 1)..].Trim();
            if (name.Length == 0 || value.Length == 0 || !parsed.TryAdd(name, value))
            {
                throw new ArgumentException($"{label} '{name}' is empty or repeated.");
            }
        }
        return parsed;
    }

    public static string[] Lines(string text) => text.Split(
        ["\r\n", "\n"],
        StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    public static void Invalid(IWin32Window owner, string message) => MessageBox.Show(
        owner,
        message,
        "Check these details",
        MessageBoxButtons.OK,
        MessageBoxIcon.Information);

    public static void ConfigureDialog(Form form)
    {
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.Font = new Font("Segoe UI", 9);
        form.FormBorderStyle = FormBorderStyle.Sizable;
        form.MaximizeBox = false;
        form.MinimizeBox = false;
        form.ShowIcon = false;
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.CenterParent;
    }
}
