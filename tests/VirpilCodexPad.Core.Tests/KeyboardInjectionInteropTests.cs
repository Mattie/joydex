using System.Reflection;
using System.Runtime.InteropServices;
using VirpilCodexPad.Core.Mapping;
using VirpilCodexPad.Windows.Actions;

namespace VirpilCodexPad.Core.Tests;

public sealed class KeyboardInjectionInteropTests
{
    [Fact]
    public void InputStructureMatchesTheWindowsAbi()
    {
        var inputType = typeof(CodexActionExecutor).GetNestedType("Input", BindingFlags.NonPublic);
        var unionType = typeof(CodexActionExecutor).GetNestedType("InputUnion", BindingFlags.NonPublic);

        Assert.NotNull(inputType);
        Assert.NotNull(unionType);
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf(inputType));
        Assert.Equal(IntPtr.Size == 8 ? 32 : 24, Marshal.SizeOf(unionType));
    }

    [Theory]
    [InlineData(CodexAction.Agent1, 0x70)]
    [InlineData(CodexAction.Agent2, 0x71)]
    [InlineData(CodexAction.Agent3, 0x72)]
    [InlineData(CodexAction.Agent4, 0x73)]
    [InlineData(CodexAction.Agent5, 0x74)]
    [InlineData(CodexAction.Agent6, 0x75)]
    [InlineData(CodexAction.ToggleFastMode, 0x76)]
    [InlineData(CodexAction.Approve, 0x77)]
    [InlineData(CodexAction.Reject, 0x78)]
    [InlineData(CodexAction.ForkTask, 0x79)]
    [InlineData(CodexAction.Submit, 0x7A)]
    [InlineData(CodexAction.TogglePlanMode, 0x7B)]
    public void CommandActionsUseStandardModifiedFunctionKeys(CodexAction action, int expectedFunctionKey)
    {
        var getShortcut = typeof(CodexActionExecutor).GetMethod("GetShortcut", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(getShortcut);
        var shortcut = Assert.IsType<ushort[]>(getShortcut.Invoke(null, [action]));
        Assert.Equal(new ushort[] { 0x11, 0x12, 0x10, (ushort)expectedFunctionKey }, shortcut);
    }

    [Theory]
    [InlineData(CodexAction.Home, 0x24)]
    [InlineData(CodexAction.End, 0x23)]
    public void HomeAndEndActionsUseUnmodifiedNavigationKeys(CodexAction action, int expectedKey)
    {
        var getShortcut = typeof(CodexActionExecutor).GetMethod("GetShortcut", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(getShortcut);
        var shortcut = Assert.IsType<ushort[]>(getShortcut.Invoke(null, [action]));
        Assert.Equal([(ushort)expectedKey], shortcut);
    }

    [Theory]
    [InlineData(CodexAction.ScrollUp, 1, 120)]
    [InlineData(CodexAction.ScrollDown, 1, -120)]
    [InlineData(CodexAction.ScrollUp, 5, 600)]
    [InlineData(CodexAction.ScrollDown, 5, -600)]
    public void ScrollActionsUseTheConfiguredMouseWheelNotches(
        CodexAction action,
        int wheelNotches,
        int expectedDelta)
    {
        var getDelta = typeof(CodexActionExecutor).GetMethod(
            "GetMouseWheelDelta",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(getDelta);
        Assert.Equal(expectedDelta, Assert.IsType<int>(getDelta.Invoke(null, [action, wheelNotches])));
    }

    [Fact]
    public void MouseWheelInputUsesTheWindowsWheelFlagAndSignedDelta()
    {
        var inputType = typeof(CodexActionExecutor).GetNestedType("Input", BindingFlags.NonPublic);
        var unionType = typeof(CodexActionExecutor).GetNestedType("InputUnion", BindingFlags.NonPublic);
        var mouseType = typeof(CodexActionExecutor).GetNestedType("MouseInput", BindingFlags.NonPublic);

        Assert.NotNull(inputType);
        Assert.NotNull(unionType);
        Assert.NotNull(mouseType);

        var factory = inputType.GetMethod("MouseWheel", BindingFlags.Public | BindingFlags.Static);
        var input = factory?.Invoke(null, [-120]);
        var data = inputType.GetField("Data")?.GetValue(input);
        var mouse = unionType.GetField("Mouse")?.GetValue(data);

        Assert.NotNull(input);
        Assert.NotNull(data);
        Assert.NotNull(mouse);
        Assert.Equal(0u, Assert.IsType<uint>(inputType.GetField("Type")?.GetValue(input)));
        Assert.Equal(unchecked((uint)-120), Assert.IsType<uint>(mouseType.GetField("MouseData")?.GetValue(mouse)));
        Assert.Equal(0x0800u, Assert.IsType<uint>(mouseType.GetField("Flags")?.GetValue(mouse)));
    }
}
