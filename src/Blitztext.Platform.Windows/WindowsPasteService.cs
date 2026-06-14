using System.Runtime.InteropServices;
using System.Windows;
using Blitztext.Core.Abstractions;

namespace Blitztext.Platform.Windows;

public sealed class WindowsPasteService : IPasteService
{
    private const int InputKeyboard = 1;
    private const uint KeyEventFKeyUp = 0x0002;
    private const uint KeyEventFUnicode = 0x0004;
    private const int SwShow = 5;

    // Modifier virtual-key codes that must not be "stuck down" when we inject text.
    private static readonly ushort[] ModifierKeys =
    [
        0x10, 0xA0, 0xA1, // Shift, LShift, RShift
        0x11, 0xA2, 0xA3, // Ctrl, LCtrl, RCtrl
        0x12, 0xA4, 0xA5, // Alt, LAlt, RAlt
        0x5B, 0x5C        // LWin, RWin
    ];

    public IntPtr CaptureCurrentTarget() => GetForegroundWindow();

    public async Task CopyAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => SetClipboard(text));
    }

    public async Task PasteAsync(string text, IntPtr targetWindow, CancellationToken cancellationToken = default)
    {
        // Snapshot whatever the user currently has on the clipboard so we can put it back
        // afterwards — using Blitztext must not clobber the user's clipboard.
        var clipboardSnapshot = await CaptureClipboardAsync();

        // Briefly place the dictated text on the clipboard so a manual Ctrl+V still works while
        // we inject; the original content is restored once injection completes.
        await CopyAsync(text, cancellationToken);

        if (targetWindow != IntPtr.Zero)
        {
            ForceForeground(targetWindow);
            await Task.Delay(60, cancellationToken);
        }

        // Inject off the UI thread (that thread owns the low-level keyboard hook, and SendInput
        // from the hook-owning thread can mis-deliver injected keys).
        await Task.Run(
            () =>
            {
                ReleaseHeldModifiers();
                System.Threading.Thread.Sleep(20);
                SendUnicodeText(text);
            },
            cancellationToken);

        // Let the injected key events drain into the target before we hand the clipboard back.
        await Task.Delay(80, cancellationToken);
        await RestoreClipboardAsync(clipboardSnapshot);
    }

    private static void SetClipboard(string text)
    {
        // WPF clipboard access fails intermittently when another process holds the clipboard;
        // retry a few times before giving up.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(text, copy: true);
                return;
            }
            catch (Exception) when (attempt < 7)
            {
                System.Threading.Thread.Sleep(40);
            }
        }
    }

    /// <summary>
    /// Copies the current clipboard contents (all readable formats) into a detached snapshot so
    /// it can be restored after we have temporarily used the clipboard for a paste.
    /// </summary>
    private static Task<System.Windows.IDataObject?> CaptureClipboardAsync()
    {
        return System.Windows.Application.Current.Dispatcher.InvokeAsync(CaptureClipboard).Task;
    }

    private static System.Windows.IDataObject? CaptureClipboard()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                var current = System.Windows.Clipboard.GetDataObject();
                if (current is null)
                {
                    return null;
                }

                var snapshot = new System.Windows.DataObject();
                var captured = false;
                foreach (var format in current.GetFormats(autoConvert: false))
                {
                    try
                    {
                        var data = current.GetData(format, autoConvert: false);
                        if (data is not null)
                        {
                            snapshot.SetData(format, data);
                            captured = true;
                        }
                    }
                    catch
                    {
                        // Some formats (e.g. live COM streams) can't be copied out; skip them.
                    }
                }

                return captured ? snapshot : null;
            }
            catch (Exception) when (attempt < 7)
            {
                System.Threading.Thread.Sleep(40);
            }
        }

        return null;
    }

    private static Task RestoreClipboardAsync(System.Windows.IDataObject? snapshot)
    {
        return System.Windows.Application.Current.Dispatcher.InvokeAsync(() => RestoreClipboard(snapshot)).Task;
    }

    private static void RestoreClipboard(System.Windows.IDataObject? snapshot)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                if (snapshot is null)
                {
                    // The clipboard was empty (or unreadable) before we touched it; clear our text
                    // rather than leaving the dictated text behind.
                    System.Windows.Clipboard.Clear();
                }
                else
                {
                    System.Windows.Clipboard.SetDataObject(snapshot, copy: true);
                }

                return;
            }
            catch (Exception) when (attempt < 7)
            {
                System.Threading.Thread.Sleep(40);
            }
        }
    }

    private static uint SendUnicodeText(string text)
    {
        var inputs = new List<Input>(text.Length * 2);
        foreach (var character in text)
        {
            // Inject every UTF-16 code unit as a Unicode key event; Chromium/Electron accept
            // these as typed text where a synthetic Ctrl+V is ignored.
            inputs.Add(UnicodeInput(character, keyUp: false));
            inputs.Add(UnicodeInput(character, keyUp: true));
        }

        return inputs.Count == 0 ? 0 : SendInput((uint)inputs.Count, [.. inputs], Marshal.SizeOf<Input>());
    }

    private static Input UnicodeInput(char character, bool keyUp)
    {
        return new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInputData
                {
                    Vk = 0,
                    Scan = character,
                    Flags = KeyEventFUnicode | (keyUp ? KeyEventFKeyUp : 0),
                    ExtraInfo = InjectedInput.Signature
                }
            }
        };
    }

    private static void ForceForeground(IntPtr hWnd)
    {
        var current = GetForegroundWindow();
        if (current == hWnd)
        {
            ShowWindow(hWnd, SwShow);
            return;
        }

        var currentThread = GetCurrentThreadId();
        var targetThread = GetWindowThreadProcessId(hWnd, out _);
        var foregroundThread = current == IntPtr.Zero ? 0u : GetWindowThreadProcessId(current, out _);

        if (foregroundThread != 0 && foregroundThread != currentThread)
        {
            AttachThreadInput(currentThread, foregroundThread, true);
        }

        if (targetThread != currentThread)
        {
            AttachThreadInput(currentThread, targetThread, true);
        }

        ShowWindow(hWnd, SwShow);
        BringWindowToTop(hWnd);
        SetForegroundWindow(hWnd);

        if (targetThread != currentThread)
        {
            AttachThreadInput(currentThread, targetThread, false);
        }

        if (foregroundThread != 0 && foregroundThread != currentThread)
        {
            AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private static void ReleaseHeldModifiers()
    {
        var ups = new List<Input>();
        foreach (var key in ModifierKeys)
        {
            if ((GetAsyncKeyState(key) & 0x8000) != 0)
            {
                ups.Add(VirtualKeyInput(key, keyUp: true));
            }
        }

        if (ups.Count > 0)
        {
            SendInput((uint)ups.Count, [.. ups], Marshal.SizeOf<Input>());
        }
    }

    private static Input VirtualKeyInput(ushort key, bool keyUp)
    {
        return new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInputData
                {
                    Vk = key,
                    Flags = keyUp ? KeyEventFKeyUp : 0,
                    ExtraInfo = InjectedInput.Signature
                }
            }
        };
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        // The native INPUT union must be sized for its largest member (MOUSEINPUT). Declaring
        // only the keyboard variant makes the struct too small on x64, so SendInput rejects the
        // call with ERROR_INVALID_PARAMETER (87) and injects nothing.
        [FieldOffset(0)]
        public MouseInputData Mouse;

        [FieldOffset(0)]
        public KeyboardInputData Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}
