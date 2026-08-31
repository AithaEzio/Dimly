// Tests the real DimEngine state machine against stand-ins for Win32 idle time and for the
// displays. Compiled by tools/enginetest.ps1 with src/DimEngine.cs, src/AppSettings.cs and
// src/Theme.cs; src/Native.cs and src/Displays.cs are replaced by the doubles below, so the
// engine under test is the shipping code, unmodified.

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

            DimEngine engine = new DimEngine(settings, displays);
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
