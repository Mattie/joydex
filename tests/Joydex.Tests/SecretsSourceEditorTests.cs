using Joydex.App;
using Joydex.Secrets;

namespace Joydex.Tests;

public sealed class SecretsSourceEditorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedSaveKeepsDraftOpenAndRetryPersistsBeforeAccepting(bool writeFailure)
    {
        // Construct an unshown editor: no windows, prompts, or tray icons are displayed.
        var directory = Path.Combine(Path.GetTempPath(), "joydex-source-editor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "synthetic.env");
            File.WriteAllText(file, "GH_TOKEN=synthetic-only\n");
            var configurationPath = Path.Combine(directory, "configuration.json");
            var store = new SecretsConfigurationStore(configurationPath);
            var aliases = new Dictionary<string, string> { ["GH_TOKEN"] = "GH_TOKEN" };
            store.UpsertEnvSource(null, "Existing", file, aliases);
            var draft = new SecretsEnvSource(Guid.NewGuid(), writeFailure ? "WWW" : "Existing", file, 1,
                [new("GH_TOKEN", "GH_TOKEN", "synthetic", 1)]);
            var failWrite = writeFailure;
            using var editor = new SecretsEnvSourceEditorDialog(draft, source =>
            {
                if (failWrite) throw new IOException("Synthetic write failure");
                store.UpsertEnvSource(null, source.DisplayName, source.FilePath, source.AliasKeys);
            });

            editor.Save();

            Assert.Equal(DialogResult.None, editor.DialogResult);
            Assert.False(editor.IsDisposed);
            Assert.Equal(draft.DisplayName, editor.DisplayName);
            Assert.Equal(file, editor.FilePath);
            Assert.Equal("GH_TOKEN", editor.AliasKeys["GH_TOKEN"]);
            Assert.Single(new SecretsConfigurationStore(configurationPath).Read().Sources);
            var error = Assert.IsType<Label>(Assert.Single(editor.Controls.Find("SecretsSourceSaveError", true)));
            Assert.Contains("not saved", error.Text);

            failWrite = false;
            Assert.IsType<TextBox>(Assert.Single(editor.Controls.Find("SecretsSourceNamespace", true))).Text = "WWW";
            editor.Save();

            Assert.Equal(DialogResult.OK, editor.DialogResult);
            Assert.Empty(error.Text);
            var persisted = new SecretsConfigurationStore(configurationPath).Read().Sources.Single(source => source.DisplayName == "WWW");
            Assert.Equal(file, persisted.FilePath);
            Assert.Equal("GH_TOKEN", Assert.Single(persisted.Aliases).Alias);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
