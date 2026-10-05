using System.Security.Cryptography;

namespace Joydex.Secrets;

/// <summary>The outcome of atomically consuming a short-lived secret-use reservation.</summary>
public enum SecretsReservationConsumeResult
{
    Consumed,
    Missing,
    Expired,
    WrongPrincipal,
    PolicyChanged,
    ScopeChanged,
}

/// <summary>Single-use in-memory reservations bound to principal, request, scope and policy epoch.</summary>
public sealed class SecretsReservationStore
{
    private readonly int _maximumReservations;
    private readonly object _gate = new();
    private readonly Dictionary<string, Reservation> _reservations = new(StringComparer.Ordinal);

    public SecretsReservationStore(int maximumReservations = 2048)
    {
        if (maximumReservations < 1) throw new ArgumentOutOfRangeException(nameof(maximumReservations));
        _maximumReservations = maximumReservations;
    }

    /// <summary>Reserves one redemption and returns an opaque handle.</summary>
    public string Reserve(
        SecretsClientPrincipal principal,
        string requestId,
        SecretsAuthorizationScope scope,
        long policyEpoch,
        DateTimeOffset expiresAt,
        DateTimeOffset? now = null,
        long operatingModeEpoch = 1)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(scope);
        if (policyEpoch < 1 || operatingModeEpoch < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(policyEpoch));
        }

        lock (_gate)
        {
            PruneExpired(now ?? DateTimeOffset.UtcNow);
            if (_reservations.Count >= _maximumReservations)
            {
                throw new InvalidOperationException("The Secrets reservation limit has been reached.");
            }
            string handle;
            do
            {
                handle = Base64Url(RandomNumberGenerator.GetBytes(32));
            }
            while (_reservations.ContainsKey(handle));
            _reservations.Add(handle, new(
                principal.RegistrationId,
                principal.Generation,
                requestId,
                scope.ExactScopeDigest,
                policyEpoch,
                operatingModeEpoch,
                expiresAt));
            return handle;
        }
    }

    internal void Prune(DateTimeOffset now)
    {
        lock (_gate) PruneExpired(now);
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var handle in _reservations
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _reservations.Remove(handle);
        }
    }

    /// <summary>Consumes at most once after checking every authority field again.</summary>
    public SecretsReservationConsumeResult Consume(
        string handle,
        SecretsClientPrincipal principal,
        string requestId,
        SecretsAuthorizationScope scope,
        long currentPolicyEpoch,
        DateTimeOffset now,
        long currentOperatingModeEpoch = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentNullException.ThrowIfNull(scope);
        lock (_gate)
        {
            if (!_reservations.Remove(handle, out var reservation))
            {
                return SecretsReservationConsumeResult.Missing;
            }
            if (reservation.ExpiresAt <= now) return SecretsReservationConsumeResult.Expired;
            if (reservation.RegistrationId != principal.RegistrationId
                || reservation.ClientGeneration != principal.Generation)
            {
                return SecretsReservationConsumeResult.WrongPrincipal;
            }
            if (!string.Equals(reservation.RequestId, requestId, StringComparison.Ordinal)
                || !FixedEquals(reservation.ExactScopeDigest, scope.ExactScopeDigest))
            {
                return SecretsReservationConsumeResult.ScopeChanged;
            }
            return reservation.PolicyEpoch == currentPolicyEpoch
                && reservation.OperatingModeEpoch == currentOperatingModeEpoch
                ? SecretsReservationConsumeResult.Consumed
                : SecretsReservationConsumeResult.PolicyChanged;
        }
    }

    private static bool FixedEquals(string left, string right) =>
        left.Length == right.Length
        && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(left),
            System.Text.Encoding.ASCII.GetBytes(right));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record Reservation(
        Guid RegistrationId,
        long ClientGeneration,
        string RequestId,
        string ExactScopeDigest,
        long PolicyEpoch,
        long OperatingModeEpoch,
        DateTimeOffset ExpiresAt);
}
