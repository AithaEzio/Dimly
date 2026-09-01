// Finds out what is resetting the system idle clock.
//
// Windows' GetLastInputInfo - which Dimly, the screen saver and the display timeout all rely
// on - is reset by any input at all. When it never climbs, something is delivering input
// constantly, and the useful question is what: a jittering mouse sensor reports movement of a
// pixel or less, while a jiggler or a remote-control session reports *injected* events. Those
// two answers point at completely different fixes, so this counts both.
//
//   inputspy.exe [seconds]

using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class InputSpy
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;

    private const int LLMHF_INJECTED = 0x01;
    private const int LLMHF_LOWER_IL_INJECTED = 0x02;
    private const int LLKHF_INJECTED = 0x10;

    private static int _moves, _movesInjected, _movesZero, _movesTiny, _movesReal;
    private static int _otherMouse, _keys, _keysInjected;
    private static int _lastX = int.MinValue, _lastY;
    private static int _maxStep;

    private static HookProc _mouseProc, _keyProc;
    private static IntPtr _mouseHook, _keyHook;

    [STAThread]
    private static void Main(string[] args)
    {
        int seconds = 10;
        if (args.Length > 0) int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds);

        _mouseProc = MouseHook;
        _keyProc = KeyHook;
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, IntPtr.Zero, 0);
        _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, IntPtr.Zero, 0);

        if (_mouseHook == IntPtr.Zero || _keyHook == IntPtr.Zero)
        {
            Console.WriteLine("Could not install the input hooks.");
            return;
        }

        Console.WriteLine("Watching raw input for " + seconds + " seconds. Do not touch anything.");

        Timer stop = new Timer();
        stop.Interval = seconds * 1000;
        stop.Tick += delegate { Application.ExitThread(); };
        stop.Start();
        Application.Run();

        UnhookWindowsHookEx(_mouseHook);
        UnhookWindowsHookEx(_keyHook);

        Console.WriteLine();
        Console.WriteLine("  mouse movements     {0}", _moves);
        Console.WriteLine("     of those injected by software  {0}", _movesInjected);
        Console.WriteLine("     zero-pixel (pure jitter)       {0}", _movesZero);
        Console.WriteLine("     one pixel                      {0}", _movesTiny);
        Console.WriteLine("     more than one pixel            {0}   (largest step {1}px)", _movesReal, _maxStep);
        Console.WriteLine("  other mouse events  {0}", _otherMouse);
        Console.WriteLine("  key events          {0}   (injected {1})", _keys, _keysInjected);
        Console.WriteLine();
        Console.WriteLine(Verdict());
    }

    private static string Verdict()
    {
        int total = _moves + _otherMouse + _keys;
        if (total == 0) return "Nothing arrived: the idle clock is free to climb, so idle detection will work.";

        if (_movesInjected > _moves / 2 && _moves > 0)
            return "Most movement is INJECTED BY SOFTWARE - a mouse jiggler, a remote-control\n"
                 + "session, or an automation tool is keeping this machine awake.";

        if (_moves > 0 && _movesReal == 0)
            return "Movement is arriving constantly but never travels more than a pixel - this is a\n"
                 + "jittering mouse sensor. Lifting the mouse onto a different surface, or unplugging\n"
                 + "it, should let the machine go idle.";

        return "Real input is arriving. If nobody is touching the machine, suspect a peripheral\n"
             + "reporting movement on its own.";
    }

    private static IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            if ((int)wParam == WM_MOUSEMOVE)
            {
                MSLLHOOKSTRUCT data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                _moves++;
                if ((data.flags & (LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) != 0) _movesInjected++;

                if (_lastX != int.MinValue)
                {
                    int step = Math.Max(Math.Abs(data.pt.x - _lastX), Math.Abs(data.pt.y - _lastY));
                    if (step == 0) _movesZero++;
                    else if (step == 1) _movesTiny++;
                    else _movesReal++;
                    if (step > _maxStep) _maxStep = step;
                }
                _lastX = data.pt.x;
                _lastY = data.pt.y;
            }
            else _otherMouse++;
        }
        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private static IntPtr KeyHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            KBDLLHOOKSTRUCT data = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
            _keys++;
            if ((data.flags & LLKHF_INJECTED) != 0) _keysInjected++;
        }
        return CallNextHookEx(_keyHook, code, wParam, lParam);
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int type, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
}
