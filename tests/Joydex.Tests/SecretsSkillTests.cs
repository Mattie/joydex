namespace Joydex.Tests;

public sealed class SecretsSkillTests
{
    [Fact]
    public void SkillUsesBrokerOperationsAndForbidsDirectSecretReads()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "skills", "joydex-secrets", "SKILL.md");
        var skill = File.ReadAllText(path);

        Assert.Contains("$helper aliases", skill, StringComparison.Ordinal);
        Assert.Contains("$helper exec", skill, StringComparison.Ordinal);
        Assert.Contains("Joydex\\profile.yaml", skill, StringComparison.Ordinal);
        Assert.Contains("Get-JoydexProfileScalar 'application_path'", skill, StringComparison.Ordinal);
        Assert.Contains("--data-root $dataRoot", skill, StringComparison.Ordinal);
        Assert.Contains("--secret AGENTMAIL_API_KEY", skill, StringComparison.Ordinal);
        Assert.Contains("--on-approval-timeout run-without-secrets", skill, StringComparison.Ordinal);
        Assert.Contains("without you needing to read their values", skill, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never open or read `.env`", skill, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Content .env", skill, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("type .env", skill, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cat .env", skill, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dotenv get", skill, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Get-Process -Name 'Joydex.App'", skill, StringComparison.OrdinalIgnoreCase);
    }
}
