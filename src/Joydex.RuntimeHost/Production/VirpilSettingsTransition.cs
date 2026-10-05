using System.Text.Json;
using Joydex.App;
using Joydex.Core.TaskAlerts;
using Joydex.Windows.TaskAlerts;

namespace Joydex.RuntimeHost.Production;

/// <summary>
/// Validates an explicit LED settings change before retiring the active hardware owner and
/// restores its external startup/profile state if the replacement is not committed.
/// </summary>
internal sealed class VirpilSettingsTransition : IDisposable
{
    private Action? _rollback;

    private VirpilSettingsTransition(Action rollback) => _rollback = rollback;

    public static VirpilSettingsTransition? Prepare(
        TaskAlertLedOptions previous,
        TaskAlertLedOptions candidate,
        Action validateDirectHardware,
        ILoginStartupStore startup,
        string profilePath,
        Action<string, TaskAlertLedOptions>? writeProfile = null)
    {
        previous = previous.Normalize();
        candidate = candidate.Normalize();
        if (JsonSerializer.Serialize(previous) == JsonSerializer.Serialize(candidate))
        {
            return null;
        }

        Action rollback;
        Action apply;
        if (candidate.Mode == TaskAlertLedOutputMode.DirectHid)
        {
            validateDirectHardware();
            var command = startup.Read();
            if (command is null)
            {
                return null;
            }
            rollback = () => startup.Write(command);
            apply = startup.Delete;
        }
        else
        {
            var existed = File.Exists(profilePath);
            var bytes = existed ? File.ReadAllBytes(profilePath) : null;
            rollback = () =>
            {
                if (existed) { File.WriteAllBytes(profilePath, bytes!); }
                else { File.Delete(profilePath); }
            };
            apply = () =>
            {
                if (writeProfile is not null) { writeProfile(profilePath, candidate); }
                else { _ = LinkToolProfileWriter.Write(profilePath, candidate); }
            };
        }

        var transition = new VirpilSettingsTransition(rollback);
        try
        {
            apply();
            return transition;
        }
        catch (Exception failure)
        {
            try { transition.Dispose(); }
            catch (Exception restoreFailure)
            {
                throw new AggregateException("LED settings preparation and restoration failed.", failure, restoreFailure);
            }
            throw;
        }
    }

    public void Commit() => _rollback = null;

    public void Dispose()
    {
        // Retain a failed rollback so a repeated cleanup never claims it succeeded.
        _rollback?.Invoke();
        _rollback = null;
    }
}
