using Joydex.Secrets;

namespace Joydex.Tests;

public sealed class SecretsAuthorizationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExactAllowDoesNotMatchChangedOperationWhileClientAllowDoes()
    {
        var original = Scope(operationDigest: Digest('1'));
        var changed = Scope(operationDigest: Digest('2'));
        var exact = SecretsPolicyRule.Create(
            SecretsConsentChoice.YesAlways,
            RememberedGrantScopeKind.ExactOperation,
            original,
            Now,
            "native-ui");
        var client = SecretsPolicyRule.Create(
            SecretsConsentChoice.YesAlways,
            RememberedGrantScopeKind.Client,
            original,
            Now,
            "native-ui");

        Assert.Equal(
            SecretsPolicyDisposition.ConsentRequired,
            SecretsPolicyEvaluator.Evaluate(changed, [exact], Now).Disposition);
        Assert.Equal(
            SecretsPolicyDisposition.Allowed,
            SecretsPolicyEvaluator.Evaluate(changed, [client], Now).Disposition);
    }

    [Fact]
    public void ClientAllowStillRejectsAnotherPrincipalProjectSecretSetOrDeliveryMode()
    {
        var original = Scope();
        var rule = SecretsPolicyRule.Create(
            SecretsConsentChoice.YesAlways,
            RememberedGrantScopeKind.Client,
            original,
            Now,
            "native-ui");

        var changed = new[]
        {
            Scope(registrationId: Guid.Parse("22222222-2222-2222-2222-222222222222")),
            Scope(projectId: "another-project"),
            Scope(aliasSecretId: "secret-two"),
            Scope(deliveryMode: SecretDeliveryMode.RawRead),
        };
        Assert.All(changed, scope => Assert.Equal(
            SecretsPolicyDisposition.ConsentRequired,
            SecretsPolicyEvaluator.Evaluate(scope, [rule], Now).Disposition));
    }

    [Fact]
    public void TwentyFourHourAllowHasFixedExpiry()
    {
        var scope = Scope();
        var rule = SecretsPolicyRule.Create(
            SecretsConsentChoice.Yes24Hours,
            RememberedGrantScopeKind.ExactOperation,
            scope,
            Now,
            "native-ui");

        Assert.Equal(Now.AddHours(24), rule.ExpiresAt);
        Assert.Equal(
            SecretsPolicyDisposition.Allowed,
            SecretsPolicyEvaluator.Evaluate(scope, [rule], Now.AddHours(23)).Disposition);
        Assert.Equal(
            SecretsPolicyDisposition.ConsentRequired,
            SecretsPolicyEvaluator.Evaluate(scope, [rule], Now.AddHours(24)).Disposition);
    }

    [Fact]
    public void ExactDenyTakesPrecedenceOverClientAllow()
    {
        var scope = Scope();
        var allow = SecretsPolicyRule.Create(
            SecretsConsentChoice.YesAlways,
            RememberedGrantScopeKind.Client,
            scope,
            Now,
            "native-ui");
        var deny = SecretsPolicyRule.Create(
            SecretsConsentChoice.Never,
            RememberedGrantScopeKind.ExactOperation,
            scope,
            Now.AddMinutes(1),
            "native-ui");

        var result = SecretsPolicyEvaluator.Evaluate(scope, [allow, deny], Now.AddMinutes(2));

        Assert.Equal(SecretsPolicyDisposition.Denied, result.Disposition);
        Assert.Equal(deny.RuleId, result.MatchedRule?.RuleId);
        Assert.Equal(RememberedGrantScopeKind.ExactOperation, deny.ScopeKind);
    }

    [Fact]
    public void RequestIdRetryJoinsOnlyIdenticalAuthorityFields()
    {
        var index = new SecretsRequestIndex();
        var original = Scope();
        var changed = Scope(operationDigest: Digest('2'));

        Assert.Equal(
            SecretsRequestRegistrationResult.Added,
            index.Register(original.Principal, "request-1", original));
        Assert.Equal(
            SecretsRequestRegistrationResult.Existing,
            index.Register(original.Principal, "request-1", original));
        Assert.Equal(
            SecretsRequestRegistrationResult.Conflict,
            index.Register(original.Principal, "request-1", changed));
    }

    [Fact]
    public void AliasOrderDoesNotChangeScopeButMappingGenerationDoes()
    {
        var forward = Scope(aliases:
        [
            new("alpha", "env", "secret-a", 1),
            new("beta", "env", "secret-b", 1),
        ]);
        var reverse = Scope(aliases:
        [
            new("beta", "env", "secret-b", 1),
            new("alpha", "env", "secret-a", 1),
        ]);
        var remapped = Scope(aliases:
        [
            new("alpha", "env", "secret-a", 2),
            new("beta", "env", "secret-b", 1),
        ]);

        Assert.Equal(forward.ExactScopeDigest, reverse.ExactScopeDigest);
        Assert.NotEqual(forward.ExactScopeDigest, remapped.ExactScopeDigest);
    }

    private static SecretsAuthorizationScope Scope(
        Guid? registrationId = null,
        string projectId = "joydex-project",
        string aliasSecretId = "secret-one",
        SecretDeliveryMode deliveryMode = SecretDeliveryMode.ExecInject,
        string? operationDigest = null,
        IEnumerable<SecretsAliasIdentity>? aliases = null,
        SecretsExecutionLifetime lifetime = SecretsExecutionLifetime.Attached) => SecretsScopeFactory.Create(
            new(
                registrationId ?? Guid.Parse("11111111-1111-1111-1111-111111111111"),
                "release-helper",
                3,
                "Release helper"),
            new(
                "joydex",
                projectId,
                Path.GetFullPath("D:\\Projects\\Joydex"),
                "worktree-main",
                Path.GetFullPath("D:\\Projects\\Joydex\\worktree"),
                7),
            aliases ?? [new("deploy-token", "env", aliasSecretId, 5)],
            new(deliveryMode, operationDigest ?? Digest('1'), 2, lifetime));

    [Fact]
    public void AttachedAndDetachedNeverShareExactOrClientScope()
    {
        var attached = Scope();
        var detached = Scope(lifetime: SecretsExecutionLifetime.Detached);
        Assert.NotEqual(attached.ExactScopeDigest, detached.ExactScopeDigest);
        Assert.NotEqual(attached.ClientScopeDigest, detached.ClientScopeDigest);
        var legacy = System.Text.Json.JsonSerializer.Deserialize<SecretsOperationIdentity>(
            "{\"DeliveryMode\":0,\"Digest\":\"" + Digest('1') + "\",\"Generation\":2}");
        Assert.Equal(SecretsExecutionLifetime.Attached, legacy!.Lifetime);
    }

    private static string Digest(char value) => new(value, 64);
}
