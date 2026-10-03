using Joydex.Core.Input;
using Joydex.Windows.Runtime;

namespace Joydex.App;

internal interface IConfigurationInputClient : IDisposable
{
    string ConnectionId { get; }
    event EventHandler<InputObservationEventArgs>? InputObserved;
    event EventHandler<InputCaptureChangedEventArgs>? CaptureChanged;
    IReadOnlyList<RuntimeInputSourceCatalogEntry> RefreshSources();
    InputSourceState? GetSourceState(string sourceId);
    InputCaptureStartResult BeginCapture(string sourceId, string purpose, long? expectedGeneration = null);
    bool ObserveForCapture(string sourceId);
    bool CancelCapture(Guid captureId);
    void ReleaseCaptureObservation(string sourceId);
}
