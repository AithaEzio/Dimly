// "Why isn't Dimly dimming?" - prints every input the engine's decision is made from, once a
// second, and appends the same lines to a log file so the answer can be captured while nobody
// is at the machine. Whichever column says BLOCKING is the reason; if none do, the idle column
// shows whether the machine is going idle at all.
//
// Built by tools/whynot.ps1 from src/Native.cs, so the idle clock and the fullscreen rule are
// the shipping ones. The audio side is implemented here rather than reused from MediaWatcher:
// two ComImport types with the same GUID in one assembly cannot both be cast to, and this tool
// needs a per-process breakdown that MediaWatcher deliberately does not collect.
//
//   whynot.exe [seconds] [logPath]

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Dimly;

internal static class WhyNot
{
    /// <summary>The same threshold MediaWatcher uses to tell content from digital silence.</summary>
    private const float AudibleThreshold = 0.0005f;

    private static StreamWriter _log;

    private static void Main(string[] args)
    {
        int seconds = 60;
        if (args.Length > 0) int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds);
        string logPath = args.Length > 1 ? args[1] : null;

        try
        {
            if (logPath != null) _log = new StreamWriter(logPath, false);

            Say("Dimly diagnostics - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Say("");
            Say("time      idle  fullscreen  audio               foreground window");
            Say("--------  ----  ----------  ------------------  -----------------------------------");

            for (int i = 0; i < seconds; i++)
            {
                Thread.Sleep(1000);

                int idle = Native.IdleMilliseconds();
                bool fullscreen = Native.IsFullscreenAppActive();

                float peak;
                List<string> sessions = ActiveSessions(out peak);
                bool audible = peak >= AudibleThreshold;

                Say(string.Format("{0}  {1,4}  {2,-10}  {3,-18}  {4}",
                    DateTime.Now.ToString("HH:mm:ss"),
                    idle / 1000,
                    fullscreen ? "BLOCKING" : "no",
                    (audible ? "BLOCKING " : "no       ") + peak.ToString("0.000000", CultureInfo.InvariantCulture),
                    Foreground()));

                // A per-process breakdown every ten seconds: enough to see who is making noise
                // without burying the table it belongs to.
                if (i % 10 == 9)
                {
                    if (sessions.Count == 0) Say("          (no active audio sessions)");
                    foreach (string session in sessions) Say("          audio: " + session);
                }
            }

            Say("");
            Say("How to read this:");
            Say("  idle       seconds since the last real key or mouse event. If this never climbs,");
            Say("             something on the machine is injecting input and nothing can ever dim.");
            Say("  fullscreen BLOCKING means 'Never dim over a fullscreen app' is holding it.");
            Say("  audio      BLOCKING means 'Never dim while media is playing' is holding it.");
            Say("             The number is the loudest active session; anything at 0.000000 is silent.");
        }
        finally
        {
            if (_log != null) { _log.Flush(); _log.Dispose(); }
        }
    }

    private static void Say(string line)
    {
        Console.WriteLine(line);
        if (_log != null) _log.WriteLine(line);
    }

    // ------------------------------------------------------------ foreground

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int capacity);

    private static string Foreground()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero) return "(none)";

        StringBuilder title = new StringBuilder(200);
        GetWindowText(window, title, title.Capacity);
        StringBuilder className = new StringBuilder(80);
        GetClassName(window, className, className.Capacity);

        string text = className + "  " + title;
        return text.Length > 66 ? text.Substring(0, 66) : text;
    }

    // ------------------------------------------------- audio, broken down by process

    private static List<string> ActiveSessions(out float loudest)
    {
        List<string> found = new List<string>();
        loudest = 0f;

        try
        {
            object raw = Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)raw;

            IMMDeviceCollection devices;
            if (enumerator.EnumAudioEndpoints(0, 1, out devices) != 0) return found;

            uint deviceCount;
            devices.GetCount(out deviceCount);

            for (uint d = 0; d < deviceCount; d++)
            {
                IMMDevice device;
                if (devices.Item(d, out device) != 0) continue;

                object managerObject;
                Guid managerId = typeof(IAudioSessionManager2).GUID;
                if (device.Activate(ref managerId, 23, IntPtr.Zero, out managerObject) != 0) continue;

                IAudioSessionEnumerator sessions;
                if (((IAudioSessionManager2)managerObject).GetSessionEnumerator(out sessions) != 0) continue;

                int sessionCount;
                sessions.GetCount(out sessionCount);

                for (int s = 0; s < sessionCount; s++)
                {
                    IAudioSessionControl2 session;
                    if (sessions.GetSession(s, out session) != 0) continue;

                    int state;
                    if (session.GetState(out state) != 0 || state != 1) continue;

                    float peak = 0f;
                    IAudioMeterInformation meter = session as IAudioMeterInformation;
                    if (meter != null) meter.GetPeakValue(out peak);
                    if (peak > loudest) loudest = peak;

                    uint processId;
                    session.GetProcessId(out processId);

                    found.Add(string.Format("{0,-26} peak {1}",
                        NameOf(processId), peak.ToString("0.000000", CultureInfo.InvariantCulture)));
                }
            }
        }
        catch (Exception error)
        {
            found.Add("(could not enumerate: " + error.Message + ")");
        }

        return found;
    }

    private static string NameOf(uint processId)
    {
        if (processId == 0) return "system sounds";
        try
        {
            return System.Diagnostics.Process.GetProcessById((int)processId).ProcessName + " (" + processId + ")";
        }
        catch (Exception) { return "pid " + processId; }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid interfaceId, uint classContext, IntPtr parameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionId, int flags, out IAudioSessionControl2 session);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionId, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // The nine IAudioSessionControl slots come first, then this interface's own additions.
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid group);
        [PreserveSig] int SetGroupingParam(ref Guid group, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notification);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint processId);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}
