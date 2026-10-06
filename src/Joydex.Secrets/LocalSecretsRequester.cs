using System.Security.Cryptography;

namespace Joydex.Secrets;

/// <summary>Prepares the helper's hidden same-user identity when it makes its first request.</summary>
public static class LocalSecretsRequester
{
    public static SecretsClientPrincipal EnsureReady(
        string dataRoot,
        string clientId,
        string displayLabel,
        string projectReference,
        string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var fullDataRoot = Path.GetFullPath(dataRoot);
        var bootstrapPath = Path.Combine(
            SecretsPaths.GetSecretsRoot(fullDataRoot),
            "local-requesters.bootstrap");
        using var bootstrapLock = SecretsFileLock.Acquire(bootstrapPath);
        var credentials = new NamedClientCredentialStore(
            SecretsPaths.GetCredentialDirectory(fullDataRoot));
        byte[] credential;
        try
        {
            credential = credentials.Load(clientId);
        }
        catch (FileNotFoundException)
        {
            credential = RandomNumberGenerator.GetBytes(32);
            credentials.Save(clientId, credential);
        }

        try
        {
            var project = SecretsProjectIdentityFactory.Create(projectReference, projectRoot);
            var registration = new NamedClientRegistry(SecretsPaths.GetClientsPath(fullDataRoot))
                .EnsureLocalRequester(
                    clientId,
                    displayLabel,
                    project,
                    SecretDeliveryMode.ExecInject,
                    credential);
            return registration.Principal;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(credential);
        }
    }
}
