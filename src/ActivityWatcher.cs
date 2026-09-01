using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Dimly
{
    /// <summary>
    /// An idle clock that only counts input a person actually produced.
    ///
    /// Windows' own <c>GetLastInputInfo</c> is reset by any HID report at all, which sounds
    /// right until a device reports on its own. A game controller with drifting analogue
    /// sticks - a single unit of flicker, sixty times a second - pins the system idle time at
    /// zero forever. Nothing that waits for idle can work on such a machine: not the screen
    /// saver, not the display timeout, and not Dimly.
    ///
    /// So this watches the two things a person unambiguously does: keyboard and mouse events,
    /// and gamepad movement past a deadzone large enough to swallow drift but far below a
    /// deliberate push of a stick. A controller sitting on the desk is then silent, while one
    /// being played with still counts as somebody being there.
    ///
    /// Input arrives through Raw Input rather than a low-level hook. Both would answer the
    /// question, but a hook is installed across every process on the machine, and
    /// <c>WH_KEYBOARD_LL</c> in particular is the API every keylogger reaches for - which is
    /// enough to get an unsigned utility flagged by antivirus heuristics. Raw Input asks
    /// Windows to post notifications to one private window instead, and Dimly only ever looks
    /// at which kind of device sent them, never at what was typed.
    /// </summary>
    public sealed class ActivityWatcher : IDisposable
    {
        /// <summary>
        /// Out of a 0-65535 axis. Drift is a unit or two; a deliberate movement is thousands.
        /// </summary>
        private const int GamepadDeadzone = 1500;

        private const int MaxGamepads = 4;

        private readonly JOYINFOEX[] _lastPad = new JOYINFOEX[MaxGamepads];
        private readonly bool[] _padKnown = new bool[MaxGamepads];

        private InputSink _sink;
        private int _lastActivityTick;
        private bool _enabled;

        // Absolute-mode pointers - remote desktop, tablets, some KVMs - report a position
        // rather than a delta, so "did it move" needs the previous position to compare with.
        private int _lastAbsoluteX;
        private int _lastAbsoluteY;
        private bool _absoluteKnown;

        /// <summary>
        /// Whether to watch at all. Turning it on creates the listener; turning it off tears it
        /// down, so an unused feature registers nothing and holds no window.
        /// </summary>
        public bool Enabled
        {
            get { return _enabled; }
            set
            {
                if (_enabled == value) return;
                _enabled = value;
                if (value) Start();
                else Stop();
            }
        }

        /// <summary>
        /// True when Raw Input registration succeeded. If it did not, this clock knows nothing,
        /// and callers must fall back to the system one rather than assume nobody is here.
        /// </summary>
        public bool Available { get { return _sink != null && _sink.Registered; } }

        /// <summary>Milliseconds since the last keystroke, mouse event or gamepad movement.</summary>
        public int IdleMilliseconds
        {
            get
            {
                if (!Available) return 0;
                int elapsed = unchecked(Environment.TickCount - _lastActivityTick);
                return elapsed < 0 ? 0 : elapsed;
            }
        }

        /// <summary>
        /// Gamepads deliver no Raw Input we ask for, so they are read on the caller's tick.
        /// Cheap enough to do every second: four reads of a driver-cached structure.
        /// </summary>
        public void PollGamepads()
        {
            if (!_enabled) return;

            for (int id = 0; id < MaxGamepads; id++)
            {
                JOYINFOEX current = new JOYINFOEX();
                current.dwSize = Marshal.SizeOf(typeof(JOYINFOEX));
                current.dwFlags = JOY_RETURNALL;

                if (joyGetPosEx(id, ref current) != JOYERR_NOERROR)
                {
                    _padKnown[id] = false;
                    continue;
                }

                if (_padKnown[id] && Moved(_lastPad[id], current)) Stamp();
                _lastPad[id] = current;
                _padKnown[id] = true;
            }
        }

        private static bool Moved(JOYINFOEX before, JOYINFOEX after)
        {
            if (before.dwButtons != after.dwButtons) return true;
            if (before.dwPOV != after.dwPOV) return true;

            return Shifted(before.dwXpos, after.dwXpos)
                || Shifted(before.dwYpos, after.dwYpos)
                || Shifted(before.dwZpos, after.dwZpos)
                || Shifted(before.dwRpos, after.dwRpos)
                || Shifted(before.dwUpos, after.dwUpos)
                || Shifted(before.dwVpos, after.dwVpos);
        }

        private static bool Shifted(int before, int after)
        {
            return Math.Abs(after - before) > GamepadDeadzone;
        }

        private void Stamp()
        {
            _lastActivityTick = Environment.TickCount;
        }

        private void Start()
        {
            _lastActivityTick = Environment.TickCount;
            _absoluteKnown = false;
            for (int id = 0; id < MaxGamepads; id++) _padKnown[id] = false;

            _sink = new InputSink(this);
            PollGamepads();
        }

        private void Stop()
        {
            if (_sink == null) return;
            _sink.Dispose();
            _sink = null;
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>Decides whether one raw mouse report represents somebody moving the mouse.</summary>
        private bool MouseDidSomething(RAWMOUSE mouse)
        {
            if (mouse.usButtonFlags != 0) return true;

            if ((mouse.usFlags & MOUSE_MOVE_ABSOLUTE) != 0)
            {
                bool moved = !_absoluteKnown || mouse.lLastX != _lastAbsoluteX || mouse.lLastY != _lastAbsoluteY;
                _lastAbsoluteX = mouse.lLastX;
                _lastAbsoluteY = mouse.lLastY;
                _absoluteKnown = true;
                return moved;
            }

            // A relative report of no distance is a twitching sensor, not a person.
            return mouse.lLastX != 0 || mouse.lLastY != 0;
        }

        /// <summary>
        /// A private, never-shown window that exists only to receive WM_INPUT. Raw Input needs
        /// a window to post to, and using one of Dimly's real windows would tie this to whether
        /// the settings window happens to be open.
        /// </summary>
        private sealed class InputSink : NativeWindow, IDisposable
        {
            private static readonly int HeaderSize = Marshal.SizeOf(typeof(RAWINPUTHEADER));
            private static readonly int BufferSize = Marshal.SizeOf(typeof(RAWINPUTHEADER)) + Marshal.SizeOf(typeof(RAWMOUSE));

            private readonly ActivityWatcher _owner;
            private IntPtr _buffer;

            public InputSink(ActivityWatcher owner)
            {
                _owner = owner;

                CreateParams parameters = new CreateParams();
                parameters.ExStyle = WS_EX_TOOLWINDOW;
                CreateHandle(parameters);

                _buffer = Marshal.AllocHGlobal(BufferSize);

                RAWINPUTDEVICE[] devices = new RAWINPUTDEVICE[2];
                devices[0].usUsagePage = HidUsagePageGeneric;
                devices[0].usUsage = HidUsageKeyboard;
                devices[0].dwFlags = RIDEV_INPUTSINK;
                devices[0].hwndTarget = Handle;
                devices[1].usUsagePage = HidUsagePageGeneric;
                devices[1].usUsage = HidUsageMouse;
                devices[1].dwFlags = RIDEV_INPUTSINK;
                devices[1].hwndTarget = Handle;

                Registered = RegisterRawInputDevices(devices, devices.Length, Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
            }

            public bool Registered { get; private set; }

            protected override void WndProc(ref Message message)
            {
                if (message.Msg == WM_INPUT) Read(message.LParam);
                base.WndProc(ref message);
            }

            private void Read(IntPtr handle)
            {
                int size = BufferSize;
                if (GetRawInputData(handle, RID_INPUT, _buffer, ref size, HeaderSize) == uint.MaxValue) return;

                RAWINPUTHEADER header = (RAWINPUTHEADER)Marshal.PtrToStructure(_buffer, typeof(RAWINPUTHEADER));

                if (header.dwType == RIM_TYPEKEYBOARD)
                {
                    // Only that a key moved, never which one: Dimly has no interest in the key.
                    _owner.Stamp();
                }
                else if (header.dwType == RIM_TYPEMOUSE)
                {
                    RAWMOUSE mouse = (RAWMOUSE)Marshal.PtrToStructure(
                        (IntPtr)(_buffer.ToInt64() + HeaderSize), typeof(RAWMOUSE));
                    if (_owner.MouseDidSomething(mouse)) _owner.Stamp();
                }
            }

            public void Dispose()
            {
                if (Registered)
                {
                    RAWINPUTDEVICE[] devices = new RAWINPUTDEVICE[2];
                    devices[0].usUsagePage = HidUsagePageGeneric;
                    devices[0].usUsage = HidUsageKeyboard;
                    devices[0].dwFlags = RIDEV_REMOVE;
                    devices[1].usUsagePage = HidUsagePageGeneric;
                    devices[1].usUsage = HidUsageMouse;
                    devices[1].dwFlags = RIDEV_REMOVE;
                    RegisterRawInputDevices(devices, devices.Length, Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
                    Registered = false;
                }

                DestroyHandle();

                if (_buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_buffer);
                    _buffer = IntPtr.Zero;
                }
            }
        }

        // ------------------------------------------------------------- Win32

        private const int WM_INPUT = 0x00FF;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        private const ushort HidUsagePageGeneric = 0x01;
        private const ushort HidUsageMouse = 0x02;
        private const ushort HidUsageKeyboard = 0x06;

        private const int RIDEV_REMOVE = 0x00000001;
        private const int RIDEV_INPUTSINK = 0x00000100;

        private const uint RID_INPUT = 0x10000003;
        private const int RIM_TYPEMOUSE = 0;
        private const int RIM_TYPEKEYBOARD = 1;

        private const ushort MOUSE_MOVE_ABSOLUTE = 0x01;

        private const int JOYERR_NOERROR = 0;
        private const int JOY_RETURNALL = 0x000000FF;

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public int dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER
        {
            public int dwType;
            public int dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWMOUSE
        {
            public ushort usFlags;
            public ushort usPadding;
            public ushort usButtonFlags;
            public ushort usButtonData;
            public uint ulRawButtons;
            public int lLastX;
            public int lLastY;
            public uint ulExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOYINFOEX
        {
            public int dwSize;
            public int dwFlags;
            public int dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos;
            public int dwButtons, dwButtonNumber, dwPOV;
            public int dwReserved1, dwReserved2;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, int count, int size);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref int size, int headerSize);

        [DllImport("winmm.dll")]
        private static extern int joyGetPosEx(int id, ref JOYINFOEX info);
    }
}
