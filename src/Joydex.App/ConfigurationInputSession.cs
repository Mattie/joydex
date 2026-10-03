using Joydex.Core.Input;
using Joydex.Windows.Runtime;

namespace Joydex.App;

/// <summary>Supplies host-owned input observation and capture to one settings editor.</summary>
internal interface IConfigurationInputSession
{
    event EventHandler<InputObservationEventArgs>? InputObserved;

    event EventHandler<InputCaptureChangedEventArgs>? CaptureChanged;

    Task<IReadOnlyList<RuntimeInputSourceCatalogEntry>> RefreshSourcesAsync(
        CancellationToken cancellationToken = default);

    InputSourceState? GetSourceState(string sourceId);

    Task<InputCaptureStartResult> BeginCaptureAsync(
        string sourceId,
        string purpose,
        long? expectedGeneration = null,
        CancellationToken cancellationToken = default);

    Task<InputCaptureChangedEventArgs?> GetCaptureAsync(
        Guid captureId,
        CancellationToken cancellationToken = default);

    Task<bool> CancelCaptureAsync(
        Guid captureId,
        CancellationToken cancellationToken = default);
}
