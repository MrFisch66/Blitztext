namespace Blitztext.Platform.Windows;

/// <summary>
/// Shared marker written to the <c>dwExtraInfo</c> field of synthetic keyboard input.
/// The global keyboard hook uses it to ignore Blitztext's own injected keystrokes (e.g. the
/// auto-paste Ctrl+V), which otherwise can be mis-delivered or re-processed by the hook.
/// </summary>
internal static class InjectedInput
{
    public static readonly IntPtr Signature = new(0x424C5A31); // "BLZ1"
}
