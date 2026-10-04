using Joydex.Contracts;
using Joydex.Core.Runtime;
using Joydex.RuntimeHost.Settings;

namespace Joydex.RuntimeHost;

internal sealed record RuntimeEngineOptions(
    RuntimeInstanceKind InstanceKind,
    string DataRoot,
    string DataRootId,
    RuntimeSettingsPaths SettingsPaths,
    IRuntimeOwnershipLeaseFactory OwnershipLeaseFactory,
    Func<RuntimeInputHost, CancellationToken, IRuntimeComposition> CompositionFactory,
    ISettingsImpactPlanner SettingsImpactPlanner,
    TimeProvider TimeProvider,
    IRuntimeSettingsProcessLauncher? SettingsProcessLauncher = null);
