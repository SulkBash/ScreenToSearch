using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace screen_to_search;

// Keep the first two values for compatibility with existing settings.json files.
internal enum HoldKey
{
    CapsLock = 0, F8 = 1,
    F1 = 0x70, F2 = 0x71, F3 = 0x72, F4 = 0x73,
    F5 = 0x74, F6 = 0x75, F7 = 0x76, F9 = 0x78,
    F10 = 0x79, F11 = 0x7A, F12 = 0x7B,
    F13 = 0x7C, F14 = 0x7D, F15 = 0x7E, F16 = 0x7F,
    F17 = 0x80, F18 = 0x81, F19 = 0x82, F20 = 0x83,
    F21 = 0x84, F22 = 0x85, F23 = 0x86, F24 = 0x87,
    Insert = 0x2D, ScrollLock = 0x91
}

internal sealed class KeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint LlkhfInjected = 0x10;
    private const uint KeyeventfKeyup = 0x0002;
    private const int InputKeyboard = 1;

    private readonly HookProcedure _procedure;
    private IntPtr _handle;
    private bool _held;
    private bool _longHold;
    private ShortcutBinding _shortcut;

    public event Action? Pressed;
    public event Action? Released;

    public KeyboardHook(ShortcutBinding shortcut)
    {
        _shortcut = shortcut;
        _procedure = Callback;
    }

    public bool IsHeld => _held;

    public void Start()
    {
        if (_handle != IntPtr.Zero) return;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        _handle = SetWindowsHookEx(WhKeyboardLl, _procedure,
            GetModuleHandle(module?.ModuleName), 0);
        if (_handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void SetShortcut(ShortcutBinding shortcut)
    {
        Reset();
        _shortcut = shortcut;
    }

    public void MarkLongHold() => _longHold = true;

    public void Reset()
    {
        _held = false;
        _longHold = false;
    }

    private IntPtr Callback(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<KeyboardData>(data);
            if (_shortcut.IsEnabled && key.VkCode == _shortcut.Key &&
                (key.Flags & LlkhfInjected) == 0)
            {
                var id = message.ToInt32();
                if (id is WmKeyDown or WmSysKeyDown)
                {
                    if (!_held && CurrentModifiers() != _shortcut.Modifiers)
                        return CallNextHookEx(_handle, code, message, data);
                    if (!_held)
                    {
                        _held = true;
                        _longHold = false;
                        Pressed?.Invoke();
                    }
                    return (IntPtr)1;
                }
                if (id is WmKeyUp or WmSysKeyUp)
                {
                    if (!_held) return CallNextHookEx(_handle, code, message, data);
                    var replay = !_longHold;
                    Reset();
                    if (replay) ReplayTap(_shortcut);
                    Released?.Invoke();
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(_handle, code, message, data);
    }

    private static ShortcutModifiers CurrentModifiers()
    {
        var modifiers = ShortcutModifiers.None;
        if (IsDown(0x11)) modifiers |= ShortcutModifiers.Control;
        if (IsDown(0x12)) modifiers |= ShortcutModifiers.Alt;
        if (IsDown(0x10)) modifiers |= ShortcutModifiers.Shift;
        if (IsDown(0x5B) || IsDown(0x5C)) modifiers |= ShortcutModifiers.Windows;
        return modifiers;
    }

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private static void ReplayTap(ShortcutBinding shortcut)
    {
        var missing = shortcut.Modifiers & ~CurrentModifiers();
        var inputs = new List<Input>(10);
        foreach (var (modifier, key) in ModifierKeys)
            if ((missing & modifier) != 0) inputs.Add(KeyInput(key, false));
        inputs.Add(KeyInput((ushort)shortcut.Key, false));
        inputs.Add(KeyInput((ushort)shortcut.Key, true));
        for (var index = ModifierKeys.Length - 1; index >= 0; index--)
        {
            var (modifier, key) = ModifierKeys[index];
            if ((missing & modifier) != 0) inputs.Add(KeyInput(key, true));
        }
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>());
    }

    private static readonly (ShortcutModifiers Modifier, ushort Key)[] ModifierKeys =
    [
        (ShortcutModifiers.Control, 0x11), (ShortcutModifiers.Alt, 0x12),
        (ShortcutModifiers.Shift, 0x10), (ShortcutModifiers.Windows, 0x5B)
    ];

    private static Input KeyInput(ushort key, bool released) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion { Keyboard = new KeyboardInput
        { VirtualKey = key, Flags = released ? KeyeventfKeyup : 0 } }
    };

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        UnhookWindowsHookEx(_handle);
        _handle = IntPtr.Zero;
        Reset();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hook, HookProcedure callback, IntPtr module, uint thread);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? module);
}
