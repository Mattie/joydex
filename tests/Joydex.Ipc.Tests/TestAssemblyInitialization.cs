using System.Runtime.CompilerServices;

namespace Joydex.Ipc.Tests;

internal static class TestAssemblyInitialization
{
    [ModuleInitializer]
    internal static void Initialize() =>
        WindowsPipePeerVerifier.TreatCurrentProcessAsUnelevatedForTests();
}
