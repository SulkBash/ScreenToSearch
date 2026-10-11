using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace screen_to_search;

// Receives physical keyboard events even when a fullscreen game uses Raw Input
// instead of the normal window message path. The low-level hook remains the
// primary path because it can suppress and replay a short shortcut tap.
internal sealed class RawKeyboardListener : NativeWindow, IDisposable
{
    private const int WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RimTypeKeyboard = 1;
    private const ushort RiKeyBreak = 0x0001;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevRemove = 0x00000001;
    private readonly Action<uint, bool> _keyChanged;
    private bool _registered;

    public RawKeyboardListener(Action<uint, bool> keyChanged)
    {
        _keyChanged = keyChanged;
        CreateHandle(new CreateParams
        {
            Caption = "Screen to Search keyboard listener",
            Style = unchecked((int)0x80000000), // WS_POPUP, initially hidden
            ExStyle = 0x00000080 // WS_EX_TOOLWINDOW
        });
        var device = new RawInputDevice
        {
            UsagePage = 0x01,
            Usage = 0x06,
            Flags = RidevInputSink,
            Target = Handle
        };
        if (!RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>()))
        {
            var error = Marshal.GetLastWin32Error();
            DestroyHandle();
            throw new Win32Exception(error, "Could not register background keyboard input.");
        }
        _registered = true;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmInput)
        {
            var size = 0u;
            var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
            if (GetRawInputData(message.LParam, RidInput, IntPtr.Zero, ref size, headerSize) == 0 &&
                size >= Marshal.SizeOf<RawInput>())
            {
                var buffer = Marshal.AllocHGlobal((int)size);
                try
                {
                    if (GetRawInputData(message.LParam, RidInput, buffer, ref size, headerSize) != uint.MaxValue)
                    {
                        var input = Marshal.PtrToStructure<RawInput>(buffer);
                        if (input.Header.Type == RimTypeKeyboard && input.Keyboard.VirtualKey != 0xFF)
                            _keyChanged(input.Keyboard.VirtualKey,
                                (input.Keyboard.Flags & RiKeyBreak) == 0);
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (_registered)
        {
            var device = new RawInputDevice
            {
                UsagePage = 0x01,
                Usage = 0x06,
                Flags = RidevRemove,
                Target = IntPtr.Zero
            };
            RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>());
            _registered = false;
        }
        DestroyHandle();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr Parameter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawKeyboard
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VirtualKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInput
    {
        public RawInputHeader Header;
        public RawKeyboard Keyboard;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices,
        uint deviceCount, uint deviceSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data,
        ref uint size, uint headerSize);
}
