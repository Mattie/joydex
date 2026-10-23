namespace Joydex.Tests;

/// <summary>
/// Keeps Room Voice layout checks from overlapping other UI test classes.
/// .NET 8's table layout allocator shares mutable MinSizeProxy and MaxSizeProxy
/// instances across threads, so separate STA threads do not isolate its sizing.
/// Other collections retain their normal parallel execution.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RoomVoiceLayoutCollection
{
    public const string Name = "Room Voice layout";
}
