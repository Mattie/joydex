using Joydex.Contracts;
using Joydex.Core.Config;
using Joydex.Core.TaskAlerts;
using Joydex.Core.Voice;

namespace Joydex.Ipc.Tests;

internal static class RuntimeIpcTestData
{
    public static RuntimeAttachRequest AttachRequest(
        RuntimeIpcEndpoint endpoint,
        RuntimeIpcLaunchTicket ticket,
        Guid? previousEpoch = null,
        long? afterSequence = null) =>
        new(
            RuntimeProtocol.MajorVersion,
            RuntimeProtocol.MinorVersion,
            RuntimeClientKind.HeadlessTest,
            endpoint.InstanceKind,
            endpoint.DataRootId,
            ticket.Value,
            previousEpoch,
            afterSequence);

    public static RuntimeSnapshot Snapshot(
        RuntimeIpcEndpoint endpoint,
        Guid? engineEpoch = null,
        long cursor = 0) =>
        new(
            engineEpoch ?? Guid.NewGuid(),
            cursor,
            new RuntimeIdentitySnapshot(
                Environment.ProcessId,
                endpoint.InstanceKind,
                endpoint.DataRootId,
                RuntimeGeneration: 1,
                Resources: []),
            Settings(),
            new RuntimeInputSnapshot([], []));

    public static SettingsSnapshot Settings()
    {
        var bundle = new SettingsBundle(
            CompanionConfig.CreateSafeDefault(),
            VoicePePreferences.Default,
            PebbleIndexPreferences.Default,
            TaskAlertPreferences.Default);
        return new SettingsSnapshot(1, bundle, bundle, [], []);
    }

    public static SettingsSnapshot LargeSettings(int textLength = 600_000)
    {
        var baseline = Settings().Desired;
        var voice = baseline.Voice with { PinnedTaskLabel = new string('x', textLength) };
        var bundle = baseline with { Voice = voice };
        return new SettingsSnapshot(2, bundle, bundle, [], []);
    }

    public static RuntimeSnapshot LargeSnapshot(RuntimeIpcEndpoint endpoint, int textLength = 600_000) =>
        Snapshot(endpoint) with { Settings = LargeSettings(textLength) };
}
