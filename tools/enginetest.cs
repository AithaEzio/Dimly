// Tests the real DimEngine state machine against stand-ins for Win32 idle time, the displays
// and the audio engine. Compiled by tools/test.ps1 alongside src/DimEngine.cs and friends;
// src/Native.cs, src/Displays.cs and src/MediaWatcher.cs are replaced by the doubles below, so
// the engine under test is the shipping code, unmodified.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

namespace Dimly
{
    /// <summary>Stands in for the Win32 idle clock.</summary>
    internal static class Native
    {
        public static int Idle;
        public static bool Fullscreen;

        public static int IdleMilliseconds() { return Idle; }
        public static bool IsFullscreenAppActive() { return Fullscreen; }
    }

    /// <summary>Stands in for the audio engine, so playback can be turned on and off at will.</summary>
    public sealed class MediaWatcher
    {
        public bool Enabled { get; set; }
        public bool IsPlaying { get; set; }
    }

    /// <summary>Stands in for the hook-based idle clock, so a machine whose system clock is
    /// pinned at zero by a self-reporting device can be simulated.</summary>
    public sealed class ActivityWatcher
    {
        public ActivityWatcher() { Available = true; }

        public bool Enabled { get; set; }
        public bool Available { get; set; }
        public int IdleMilliseconds { get; set; }
        public int Polls;

        public void PollGamepads() { Polls++; }
    }

    public enum BrightnessKind { Backlight, Ddc, Overlay }

    /// <summary>A display that only remembers what it was told.</summary>
    public sealed class DisplayTarget
    {
        public DisplayTarget(string key, int startingBrightness)
        {
            Key = key;
            Name = key;
            Hardware = startingBrightness;
        }

        public string Key { get; private set; }
        public string Name { get; private set; }
        public int Hardware;
        public int Writes;

        public int? Captured { get; set; }
        public int? Applied { get; set; }

        public bool TryRead(out int percent) { percent = Hardware; return true; }
        public void Write(int percent) { Hardware = percent; Writes++; }
    }

    public sealed class DisplayManager
    {
        private readonly List<DisplayTarget> _targets = new List<DisplayTarget>();
        public IList<DisplayTarget> Targets { get { return _targets; } }
        public void Add(DisplayTarget target) { _targets.Add(target); }
        public void Refresh() { }
    }

    internal static class EngineTest
    {
        private static int _failures;

        [STAThread]
        private static void Main()
        {
            AppSettings settings = new AppSettings();
            settings.IdleSeconds = 3;
            settings.AwayBrightness = 25;
            settings.Fade = false;
            settings.SkipFullscreen = false;
            settings.DimOnLock = true;

            DisplayManager displays = new DisplayManager();
            DisplayTarget bright = new DisplayTarget("bright", 100);
            DisplayTarget dim = new DisplayTarget("already-dim", 20);
            displays.Add(bright);
            displays.Add(dim);

            MediaWatcher media = new MediaWatcher();
            ActivityWatcher activity = new ActivityWatcher();
            DimEngine engine = new DimEngine(settings, displays, media, activity);
            engine.Start();

            Native.Idle = 0;
            Pump(1400);
            Check("starts awake", engine.State == DimState.Awake && bright.Hardware == 100);

            Native.Idle = 3500;
            Pump(2000);
            Check("dims after the delay", engine.State == DimState.Dimmed && bright.Hardware == 25);
            Check("leaves a darker display alone", dim.Hardware == 20);

            Native.Idle = 0;
            Pump(2000);
            Check("restores on input", engine.State == DimState.Awake && bright.Hardware == 100);

            // Fullscreen guard
            settings.SkipFullscreen = true;
            Native.Fullscreen = true;
            Native.Idle = 9000;
            Pump(2000);
            Check("skips a fullscreen app", engine.State == DimState.Awake && bright.Hardware == 100);
            Native.Fullscreen = false;
            Pump(1600);
            Check("dims once fullscreen ends", engine.State == DimState.Dimmed);
            settings.SkipFullscreen = false;

            // Pause
            engine.Paused = true;
            Pump(900);
            Check("pausing restores", engine.State == DimState.Awake && bright.Hardware == 100);
            Pump(1600);
            Check("stays awake while paused", bright.Hardware == 100);
            engine.Paused = false;
            Pump(1800);
            Check("resuming dims again", engine.State == DimState.Dimmed && bright.Hardware == 25);

            // Lock and unlock
            Native.Idle = 0;
            Pump(1600);
            Check("awake before locking", engine.State == DimState.Awake);
            engine.SetLocked(true);
            Pump(900);
            Check("dims when locked even at zero idle", engine.State == DimState.Dimmed && bright.Hardware == 25);
            engine.SetLocked(false);
            Pump(900);
            Check("restores when unlocked", engine.State == DimState.Awake && bright.Hardware == 100);

            // A device reporting on its own - a drifting gamepad - pins the system idle clock
            // at zero. Without help, nothing can ever dim; that is the bug this guards against.
            Native.Idle = 0;
            activity.IdleMilliseconds = 20000;
            Pump(1800);
            Check("system clock alone never dims on such a machine", engine.State == DimState.Awake);

            settings.IgnoreNoisyDevices = true;
            Pump(2600);
            Check("counting real input instead lets it dim", engine.State == DimState.Dimmed && bright.Hardware == 25);
            Check("gamepads are polled every tick", activity.Polls > 0);
            Check("the watcher is switched on with the setting", activity.Enabled);

            activity.IdleMilliseconds = 0;
            Pump(1800);
            Check("real input still restores", engine.State == DimState.Awake && bright.Hardware == 100);

            // With no hooks there is nothing to trust, so the system clock has to be believed.
            activity.Available = false;
            activity.IdleMilliseconds = 20000;
            Pump(2600);
            Check("falls back to the system clock when hooks are unavailable", engine.State == DimState.Awake);
            activity.Available = true;
            settings.IgnoreNoisyDevices = false;
            activity.IdleMilliseconds = 0;
            Pump(1600);

            // Media playing holds the countdown at the start line
            settings.HoldWhileAudioPlays = true;
            Native.Idle = 0;
            Pump(1600);
            media.IsPlaying = true;
            Native.Idle = 9000;
            Pump(2600);
            Check("media playing blocks the dim", engine.State == DimState.Awake && bright.Hardware == 100);
            Check("status reports the hold", engine.HeldByMedia);
            Check("countdown is pinned at zero", engine.CountdownSeconds == 0);

            // ... and the countdown restarts from the moment it stops, rather than dimming at once
            media.IsPlaying = false;
            Pump(1600);
            Check("no instant dim when playback stops", engine.State == DimState.Awake);
            Check("hold released", !engine.HeldByMedia);
            Pump(2600);
            Check("dims once the delay passes without sound", engine.State == DimState.Dimmed && bright.Hardware == 25);

            // Turning the toggle off makes sound irrelevant again
            Native.Idle = 0;
            Pump(1600);
            settings.HoldWhileAudioPlays = false;
            media.IsPlaying = true;
            Native.Idle = 9000;
            Pump(2600);
            Check("toggle off ignores playback", engine.State == DimState.Dimmed && bright.Hardware == 25);
            Check("watcher switched off with the toggle", !media.Enabled);
            media.IsPlaying = false;
            Native.Idle = 0;
            Pump(1600);

            // Manual override: dims on demand and ignores the mouse until switched off
            Native.Idle = 0;
            engine.Overridden = true;
            Pump(1800);
            Check("override dims at zero idle", engine.State == DimState.Dimmed && bright.Hardware == 25);
            Pump(1600);
            Check("override ignores activity", engine.State == DimState.Dimmed && bright.Hardware == 25);

            settings.AwayBrightness = 60;
            engine.Reapply();
            Pump(700);
            Check("override follows a new level", bright.Hardware == 60);

            engine.Overridden = false;
            Pump(900);
            Check("override restores the old brightness", engine.State == DimState.Awake && bright.Hardware == 100);

            // Pausing has to win over an override the user forgot about
            engine.Overridden = true;
            Pump(900);
            engine.Paused = true;
            Pump(900);
            Check("pause clears a forgotten override", bright.Hardware == 100 && !engine.Overridden);
            engine.Paused = false;
            Pump(900);
            Check("override does not come back by itself", bright.Hardware == 100);

            // A display switched off mid-dim is handed back
            settings.AwayBrightness = 25;
            Native.Idle = 9000;
            Pump(1800);
            Check("dimmed again", bright.Hardware == 25);
            settings.DisabledDisplays.Add("bright");
            engine.Reapply();
            Pump(700);
            Check("excluding a display restores it", bright.Hardware == 100);
            settings.DisabledDisplays.Clear();
            engine.Reapply();
            Pump(700);
            Check("re-including a display dims it again", bright.Hardware == 25);

            // Shutdown always hands the hardware back
            Check("still dimmed before shutdown", engine.State == DimState.Dimmed);
            engine.ShutdownRestore();
            Check("shutdown restores even from an override", bright.Hardware == 100 && dim.Hardware == 20);

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : _failures + " CHECK(S) FAILED");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }

        private static void Check(string what, bool passed)
        {
            Console.WriteLine((passed ? "  pass  " : "  FAIL  ") + what);
            if (!passed) _failures++;
        }

        /// <summary>Runs the message loop so the engine's timer ticks, without blocking it.</summary>
        private static void Pump(int milliseconds)
        {
            int until = Environment.TickCount + milliseconds;
            while (Environment.TickCount < until)
            {
                Application.DoEvents();
                Thread.Sleep(15);
            }
        }
    }
}
