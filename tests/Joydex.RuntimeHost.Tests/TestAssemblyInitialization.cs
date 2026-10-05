using System.Runtime.CompilerServices;
using Joydex.Ipc;

namespace Joydex.RuntimeHost.Tests;

internal static class TestAssemblyInitialization
{
    [ModuleInitializer]
    internal static void Initialize() =>
        WindowsPipePeerVerifier.TreatCurrentProcessAsUnelevatedForTests();
}
