using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using Blitztext.Core.Abstractions;
using Blitztext.Core.Models;

namespace Blitztext.Platform.Windows;

/// <summary>
/// Global hotkey handling built on a low-level keyboard hook (WH_KEYBOARD_LL).
/// A hook is used instead of RegisterHotKey so that modifier-only combinations
/// such as Ctrl + Win can be used and so that key-release is observed directly
/// (no GetAsyncKeyState polling) for reliable push-to-talk.
/// </summary>
public sealed class WindowsHotkeyService : IHotkeyService
{
    private const int WhKeyboardLL = 13;
    private const int HcAction = 0;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int VkLShift = 0xA0;
    private const int VkRShift = 0xA1;
    private const int VkLControl = 0xA2;
    private const int VkRControl = 0xA3;
    private const int VkLMenu = 0xA4;
    private const int VkRMenu = 0xA5;

    private readonly LowLevelKeyboardProc _proc;
    private readonly List<HotkeyRegistration> _registrations = [];
    private IntPtr _hookHandle = IntPtr.Zero;
    private Dispatcher? _dispatcher;

    private bool _ctrl;
    private bool _alt;
    private bool _shift;
    private bool _win;
    private bool _paused;
    private HotkeyRegistration? _active;

    public WindowsHotkeyService()
    {
        // Keep a field reference so the delegate is not garbage collected while hooked.
        _proc = HookCallback;
    }

    public HotkeyMode Mode { get; set; } = HotkeyMode.Hold;

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (value)
            {
                _active = null;
            }
        }
    }

    public event EventHandler<HotkeyEvent>? Hotkey;

    public void Configure(IReadOnlyDictionary<WorkflowType, HotkeyBinding> bindings)
    {
        var next = new List<HotkeyRegistration>();
        foreach (var (workflowType, binding) in bindings)
        {
            if (TryCreateRegistration(workflowType, binding, out var registration))
            {
                next.Add(registration);
            }
        }

        // Match more specific combinations first (e.g. Ctrl + Alt + Shift before Ctrl + Alt).
        next.Sort((left, right) => right.Binding.ModifierCount.CompareTo(left.Binding.ModifierCount));

        _registrations.Clear();
        _registrations.AddRange(next);
        _active = null;
    }

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            return;
        }

        _dispatcher = Dispatcher.CurrentDispatcher;
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        using var module = process.MainModule;
        var moduleHandle = module is null ? IntPtr.Zero : GetModuleHandle(module.ModuleName);
        _hookHandle = SetWindowsHookEx(WhKeyboardLL, _proc, moduleHandle, 0);
    }

    public void Stop()
    {
        if (_hookHandle == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
        _active = null;
        _ctrl = _alt = _shift = _win = false;
    }

    public void Dispose() => Stop();

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != HcAction)
        {
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        var message = wParam.ToInt32();
        var isDown = message is WmKeyDown or WmSysKeyDown;
        var isUp = message is WmKeyUp or WmSysKeyUp;

        if (isDown || isUp)
        {
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);

            // Ignore Blitztext's own injected keystrokes (e.g. the auto-paste Ctrl+V) so the
            // hook neither swallows them nor pollutes its modifier state.
            if (data.dwExtraInfo == InjectedInput.Signature)
            {
                return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            if (_paused)
            {
                // Keep modifier state fresh, but neither match nor swallow while paused.
                UpdateModifierState((int)data.vkCode, isDown);
            }
            else if (ProcessKey((int)data.vkCode, isDown))
            {
                return 1;
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    /// <summary>Returns true when the key event should be swallowed (not forwarded to other apps).</summary>
    private bool ProcessKey(int virtualKey, bool isDown)
    {
        var isModifier = UpdateModifierState(virtualKey, isDown);

        if (_active is not null)
        {
            if (IsReleaseOf(_active, virtualKey, isDown))
            {
                var released = _active;
                _active = null;
                Raise(HotkeyEventKind.Up, released.WorkflowType);

                // Swallow the trailing key-up of the regular key so it does not leak.
                return !released.Binding.IsModifierOnly && !isDown && virtualKey == released.VirtualKey;
            }

            // Swallow key auto-repeat of the regular key while the combo is held.
            return !_active.Binding.IsModifierOnly && isDown && virtualKey == _active.VirtualKey;
        }

        if (!isDown)
        {
            return false;
        }

        if (isModifier)
        {
            // Modifier-only combos fire the moment the exact modifier set is complete.
            foreach (var registration in _registrations)
            {
                if (registration.Binding.IsModifierOnly && ModifiersMatch(registration.Binding))
                {
                    _active = registration;
                    Raise(HotkeyEventKind.Down, registration.WorkflowType);
                    return false; // never swallow modifier keys
                }
            }

            return false;
        }

        // Regular key with an exact modifier match.
        foreach (var registration in _registrations)
        {
            if (!registration.Binding.IsModifierOnly &&
                virtualKey == registration.VirtualKey &&
                ModifiersMatch(registration.Binding))
            {
                _active = registration;
                Raise(HotkeyEventKind.Down, registration.WorkflowType);
                return true;
            }
        }

        return false;
    }

    private bool IsReleaseOf(HotkeyRegistration registration, int virtualKey, bool isDown)
    {
        if (registration.Binding.IsModifierOnly)
        {
            return !ModifiersMatch(registration.Binding);
        }

        if (!isDown && virtualKey == registration.VirtualKey)
        {
            return true;
        }

        return !ModifiersMatch(registration.Binding);
    }

    private bool UpdateModifierState(int virtualKey, bool isDown)
    {
        switch (virtualKey)
        {
            case VkControl:
            case VkLControl:
            case VkRControl:
                _ctrl = isDown;
                return true;
            case VkMenu:
            case VkLMenu:
            case VkRMenu:
                _alt = isDown;
                return true;
            case VkShift:
            case VkLShift:
            case VkRShift:
                _shift = isDown;
                return true;
            case VkLWin:
            case VkRWin:
                _win = isDown;
                return true;
            default:
                return false;
        }
    }

    private bool ModifiersMatch(HotkeyBinding binding) =>
        _ctrl == binding.Control &&
        _alt == binding.Alt &&
        _shift == binding.Shift &&
        _win == binding.Windows;

    private void Raise(HotkeyEventKind kind, WorkflowType workflowType)
    {
        var hotkeyEvent = new HotkeyEvent(kind, workflowType);
        var dispatcher = _dispatcher;
        if (dispatcher is null)
        {
            Hotkey?.Invoke(this, hotkeyEvent);
            return;
        }

        // Queue so the hook callback returns immediately (it must stay well under the
        // system LowLevelHooksTimeout) and workflow start/stop runs on the UI thread.
        dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Hotkey?.Invoke(this, hotkeyEvent)));
    }

    private static bool TryCreateRegistration(
        WorkflowType workflowType,
        HotkeyBinding binding,
        out HotkeyRegistration registration)
    {
        registration = default!;
        if (!binding.IsUsable())
        {
            return false;
        }

        var virtualKey = 0;
        if (!binding.IsModifierOnly && !TryGetVirtualKey(binding.Key, out virtualKey))
        {
            return false;
        }

        registration = new HotkeyRegistration(workflowType, binding, virtualKey);
        return true;
    }

    private static bool TryGetVirtualKey(string keyName, out int virtualKey)
    {
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(keyName))
        {
            return false;
        }

        if (Enum.TryParse<Key>(keyName, ignoreCase: true, out var key))
        {
            virtualKey = KeyInterop.VirtualKeyFromKey(key);
            return virtualKey > 0;
        }

        if (keyName.Length == 1)
        {
            var c = char.ToUpperInvariant(keyName[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = c;
                return true;
            }
        }

        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private sealed record HotkeyRegistration(WorkflowType WorkflowType, HotkeyBinding Binding, int VirtualKey);
}
