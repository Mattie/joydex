namespace Joydex.Windows.Input;

public sealed record DirectInputDeviceInfo(
    string InstanceName,
    string ProductName,
    Guid InstanceGuid,
    Guid ProductGuid);
