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

        /// <summary>How many times the working set has been handed back to Windows.</summary>
        public static int Trims;

        public static int IdleMilliseconds() { return Idle; }
        public static bool IsFullscreenAppActive() { return Fullscreen; }
        public static void TrimMemory() { Trims++; }
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
            ScreenOn = true;
        }

        public string Key { get; private set; }
        public string Name { get; private set; }
        public int Hardware;
        public int Writes;

        public int? Captured { get; set; }
        public int? Applied { get; set; }
        public int? LastAwakeLevel { get; set; }
        public bool ScreenOn { get; set; }

        /// <summary>A value the display quietly refuses, to stand in for hardware that accepts
        /// a command and does nothing with it.</summary>
        public int? Rejects;

        public int Reacquisitions;

        public int Reads;

        public bool TryRead(out int percent) { Reads++; percent = Hardware; return Readable; }
        public bool Readable = true;

        public void Write(int percent)
        {
            Writes++;
            if (Rejects.HasValue && Rejects.Value == percent) return;
            Hardware = percent;
        }

        public bool TryWriteVerified(int percent)
        {
            Write(percent);
            return Readable && Hardware == percent;
        }

        public void Reacquire() { Reacquisitions++; }
    }

    public sealed class DisplayManager
    {
        private readonly List<DisplayTarget> _targets = new List<DisplayTarget>();
        public IList<DisplayTarget> Targets { get { return _targets; } }
        public void Add(DisplayTarget target) { _targets.Add(target); target.ScreenOn = _screenOn; }

        private bool _screenOn = true;

        /// <summary>Whether Windows has the screen on, handed down to every display.</summary>
        public bool ScreenOn
        {
            get { return _screenOn; }
            set
            {
                _screenOn = value;
                foreach (DisplayTarget target in _targets) target.ScreenOn = value;
            }
        }
        public void Refresh() { Refreshes++; }

        public int Refreshes;
        public int Reacquisitions;

        /// <summary>How many times the displays have been asked whether they are awake.</summary>
        public int Enquiries;

        /// <summary>Stands in for a monitor in power save: silent until asked this many times.</summary>
        public int AnswersAfter;

        public bool AllAnswering()
        {
            Enquiries++;
            return Enquiries > AnswersAfter;
        }

        /// <summary>How long a rescan pretends to take, so the hold can be observed.</summary>
        public int RescanMilliseconds;

        /// <summary>What a rescan reports: whether the list of displays itself changed.</summary>
        public bool ListChanges;

        public bool Reacquire()
        {
            Reacquisitions++;
            if (RescanMilliseconds > 0) Thread.Sleep(RescanMilliseconds);
            foreach (DisplayTarget target in _targets) target.Reacquire();
            return ListChanges;
        }

        /// <summary>
        /// Work the engine wants run on the thread that owns the windows. The test drives
        /// everything from one thread, so it is queued and run by the next pump - which is what
        /// BeginInvoke amounts to in the real app.
        /// </summary>
        public static readonly Queue<MethodInvoker> Pending = new Queue<MethodInvoker>();

        public void OnUiThread(MethodInvoker work)
        {
            lock (Pending) Pending.Enqueue(work);
        }

        /// <summary>Runs whatever the engine handed over, on the caller's thread.</summary>
        public static void DrainPending()
        {
            for (; ; )
            {
                MethodInvoker work;
                lock (Pending)
                {
                    if (Pending.Count == 0) return;
                    work = Pending.Dequeue();
                }
                work();
            }
        }
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

            // Auto restore ships on, and is what the section near the end exercises. Everything
            // before it is about capturing and putting back a display's own brightness, so those
            // scenarios say plainly which mode they are in rather than inheriting the default.
            Check("auto restore is on by default", settings.IsAutoRestore(bright));
            settings.SetAutoRestore(bright, false);
            settings.SetAutoRestore(dim, false);

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

            // --- a display that undoes the restore behind Dimly's back -------------------
            // This is what a monitor does when it finishes powering on: it accepts the restore,
            // confirms it when read back, then reloads the brightness it had a few seconds later.
            Settle(engine, bright, 100);
            Native.Idle = 9000;
            Pump(1800);
            Check("dimmed ready for the wake test", bright.Hardware == 25);

            Native.Idle = 0;
            Pump(1800);
            Check("restored on the way back", bright.Hardware == 100);

            bright.Hardware = 25;                       // the display quietly undoes it
            Check("a display that undoes the restore is put back",
                WaitFor(delegate { return bright.Hardware == 100; }, 6000));

            // --- the fallback, for a level the display will not take ----------------------
            settings.RestoreFallback = 80;
            Settle(engine, bright, 100);
            Native.Idle = 9000;
            Pump(1800);
            Check("dimmed before refusing", bright.Hardware == 25);

            bright.Rejects = 100;                       // it will not go back to where it was
            Native.Idle = 0;
            Check("falls back when the old level will not take",
                WaitFor(delegate { return bright.Hardware == 80; }, 5000));

            // A display given a level of its own is put there rather than at the shared one.
            settings.SetFallbackFor("bright", 65);
            Settle(engine, bright, 100);
            Native.Idle = 9000;
            Pump(1800);
            Check("dimmed before refusing again", bright.Hardware == 25);

            bright.Rejects = 100;
            Native.Idle = 0;
            Check("a display's own fallback wins over the shared one",
                WaitFor(delegate { return bright.Hardware == 65; }, 5000));
            settings.DisplayFallbacks.Clear();

            // --- a refused restore is remembered, and offered again later -----------------
            settings.RestoreFallback = 90;              // same as captured, so only a retry can fix it
            Settle(engine, bright, 90);
            Native.Idle = 9000;
            Pump(1800);
            Check("dimmed before going deaf", bright.Hardware == 25);

            bright.Rejects = 90;
            bright.Readable = false;                    // refuses everything and says nothing
            Native.Idle = 0;
            Pump(2400);
            Check("an unrestorable display keeps what it knows", bright.Captured.HasValue);

            bright.Rejects = null;
            bright.Readable = true;
            bool answered = WaitFor(delegate { return bright.Hardware == 90; }, 12000);
            if (!answered)
                Console.WriteLine("      hardware=" + bright.Hardware
                    + " captured=" + bright.Captured + " applied=" + bright.Applied
                    + " writes=" + bright.Writes + " state=" + engine.State
                    + " paused=" + engine.Paused + " overridden=" + engine.Overridden);
            Check("and is put right once it answers again", answered);

            // --- a page watching the engine must not cancel the work it reports on --------
            // The Displays tab reads every level again whenever the engine changes state. That
            // refresh used to cancel the dim which had only just been queued, so the app said
            // "Dimmed" while every screen stayed exactly as bright as before.
            // Faded, because that is where it bites: a fade checks for cancellation between
            // every step, so a cancelled dim stops before it has visibly moved anything.
            Settle(engine, bright, 100);
            settings.Fade = true;
            settings.FadeMillis = 700;
            EventHandler watching = delegate
            {
                engine.ReadLevels(delegate(Dictionary<string, int> ignored) { });
            };
            engine.Changed += watching;

            Native.Idle = 9000;
            Check("a page reading levels on every change does not cancel the dim",
                WaitFor(delegate { return bright.Hardware == 25; }, 6000));

            Native.Idle = 0;
            Check("nor the restore that follows it",
                WaitFor(delegate { return bright.Hardware == 100; }, 6000));
            engine.Changed -= watching;

            // A reading reports where the display actually is, which is what the page promises.
            Dictionary<string, int> reported = null;
            engine.ReadLevels(delegate(Dictionary<string, int> levels) { reported = levels; });
            WaitFor(delegate { return reported != null; }, 4000);
            Check("a reading reports what the display is set to",
                reported != null && reported.ContainsKey("bright") && reported["bright"] == 100);

            // A reading must survive whatever the engine does next. One abandoned half way
            // never reaches the page, which then goes on showing a level the display left
            // behind - the stale reading that made a restored screen still read as dimmed.
            Dictionary<string, int> late = null;
            engine.ReadLevels(delegate(Dictionary<string, int> levels) { late = levels; });
            engine.Reapply();
            engine.Reapply();
            Check("a reading still arrives when more work follows it",
                WaitFor(delegate { return late != null; }, 6000));

            // One that will not answer is left out entirely, rather than reported as zero.
            bright.Readable = false;
            bright.Captured = null;
            reported = null;
            engine.ReadLevels(delegate(Dictionary<string, int> levels) { reported = levels; });
            WaitFor(delegate { return reported != null; }, 4000);
            Check("a display that will not answer is left out of the reading",
                reported != null && !reported.ContainsKey("bright"));
            bright.Readable = true;
            settings.Fade = false;

            // --- the screen was switched off by Windows, and has come back -----------------
            // Not a sleep, so nothing else tells Dimly. The handle it holds survives the
            // power-off in name only: it takes writes, ignores them, and echoes them back, so
            // the restore made when the user touches the mouse can be confirmed while the panel
            // is still coming up dim. The display coming back is its own event.
            Settle(engine, bright, 100);
            Native.Idle = 9000;
            Pump(1800);
            Check("dimmed before the screen went off", bright.Hardware == 25);

            Native.Idle = 0;
            Check("and restored when the user came back",
                WaitFor(delegate { return bright.Hardware == 100; }, 6000));

            // Long enough after that the restore is no longer being watched over.
            Settle(engine, bright, 100);

            bright.Hardware = 25;                       // it comes up dim anyway
            bright.Reacquisitions = 0;
            engine.OnDisplayPowerOn();
            Check("a display that comes back dim after the screen was off is put right",
                WaitFor(delegate { return bright.Hardware == 100; }, 8000));
            Check("and its handle was taken again first", bright.Reacquisitions > 0);

            // Still dimmed when the screen comes back? Then it stays dimmed.
            Settle(engine, bright, 100);
            Native.Idle = 9000;
            Pump(1800);
            bright.Hardware = 25;
            engine.OnDisplayPowerOn();
            Pump(2500);
            Check("one that comes back while still away is left dimmed", bright.Hardware == 25);
            Native.Idle = 0;
            Pump(2000);

            // --- while Windows has the screen off, nothing is touched -----------------------
            // A monitor being powered down refuses every command, and acting on those refusals
            // is how a working display gets written off as broken and covered with an overlay.
            Settle(engine, bright, 100);
            Native.Idle = 0;
            Pump(1200);

            engine.SetScreenOn(false);
            bright.Writes = 0;
            Native.Idle = 9000;                    // away, so it would normally dim
            Pump(2500);
            Check("nothing is written while the screen is off", bright.Writes == 0);
            Check("and the display was told the screen is off", !bright.ScreenOn);

            engine.SetScreenOn(true);
            Check("the display is told when the screen comes back", bright.ScreenOn);
            Check("and dimming resumes once it is back",
                WaitFor(delegate { return bright.Hardware == 25; }, 8000));
            Native.Idle = 0;
            Pump(2000);

            // --- Smart restore: nothing is decided until the displays have been checked -----
            // A monitor still powering up answers with values it will contradict a second
            // later, so the dim is held through the check even though the user is already
            // back, and the brightness comes back once rather than twice.
            Settle(engine, bright, 100);
            Native.Idle = 9000;
            Pump(1800);
            Check("dimmed before the screen switched off", bright.Hardware == 25);

            displays.Reacquisitions = 0;
            displays.Refreshes = 0;
            displays.Enquiries = 0;
            displays.AnswersAfter = 3;             // a monitor that takes its time waking up

            engine.SmartRestore();
            Native.Idle = 0;                       // back at the desk straight away
            Pump(1200);
            Check("nothing is even asked of a monitor while it settles", displays.Enquiries == 0);
            Check("the screen is held dim while the displays wake up",
                engine.AwaitingRescan && bright.Hardware == 25);

            Check("and the brightness comes back once they answer",
                WaitFor(delegate { return bright.Hardware == 100; }, 20000));
            Check("it waited for them rather than writing into the dark",
                displays.Enquiries > displays.AnswersAfter);
            Check("the check ran once, and rebuilt nothing it did not have to",
                displays.Reacquisitions == 1 && displays.Refreshes == 0);
            Check("and it is no longer holding anything back", !engine.AwaitingRescan);

            displays.AnswersAfter = 0;

            // Something else asking the engine for work while the check is running cancels it.
            // Windows announces a display change twice the instant the screen comes back, so
            // this is not a corner case - it is what happens every time. A check cut short must
            // still let the screen go: leaving the hold set stops every restore for good, and
            // the screen stays dark with the app insisting it is still checking.
            Settle(engine, bright, 100);
            Native.Idle = 9000;
            Pump(1800);
            Check("dimmed before the check is interrupted", bright.Hardware == 25);

            displays.AnswersAfter = 100;           // a monitor that never answers
            engine.SmartRestore();
            Pump(400);
            Check("the check is holding the screen", engine.AwaitingRescan);

            // Settings being saved asks the engine to re-apply, and that must not disturb a
            // check in progress - nothing is written to a monitor that is still waking anyway.
            engine.Reapply();
            Pump(600);
            Check("routine work does not interrupt the check", engine.AwaitingRescan);

            // Something that really does take the displays cuts it short, and the hold must
            // still be let go of: leaving it set stops every restore for good, and the screen
            // stays dark with the app insisting it is still checking.
            engine.OnResume();
            Native.Idle = 0;                       // and the user is back
            Check("a check cut short still lets go of the screen",
                WaitFor(delegate { return !engine.AwaitingRescan; }, 8000));
            Check("and the brightness comes back",
                WaitFor(delegate { return bright.Hardware == 100; }, 8000));

            displays.AnswersAfter = 0;

            // The screen comes back at the dimmed level after the power-off, with Dimly awake
            // and holding nothing: the check must still put right what it last set. This is the
            // stuck-dim monitor, and Smart restore has to handle it as the plain path does.
            Settle(engine, bright, 100);
            bright.Hardware = 25;
            engine.SmartRestore();
            Check("a screen that comes back dim is put right by the check too",
                WaitFor(delegate { return bright.Hardware == 100; }, 8000));

            // Still dimmed when the check finishes? Then it stays dimmed.
            Settle(engine, bright, 100);
            Native.Idle = 9000;
            Pump(1800);
            engine.SmartRestore();
            Pump(1500);
            Check("a screen still left alone stays dimmed after the check", bright.Hardware == 25);
            Native.Idle = 0;
            Pump(2000);

            // --- auto restore: no reading at all, there and back ---------------------------
            // The display is never asked how bright it is. It goes to the away level and comes
            // back to the level chosen for it, which is the whole point of the setting.
            settings.RestoreFallback = 100;
            settings.SetAutoRestore(bright, true);
            Settle(engine, bright, 100);
            settings.SetFallbackFor("bright", 70);
            Native.Idle = 0;
            Pump(1600);

            bright.Reads = 0;
            Native.Idle = 9000;
            Pump(2000);
            Check("auto restore dims to the away level", bright.Hardware == 25);
            Check("and never asks the display how bright it was", bright.Reads == 0);

            Native.Idle = 0;
            Check("auto restore comes back to the chosen level",
                WaitFor(delegate { return bright.Hardware == 70; }, 6000));

            // A display already darker than the away level is still taken to it: nothing was
            // read, so there is nothing to compare against.
            Settle(engine, bright, 10);
            Native.Idle = 9000;
            Pump(2000);
            Check("a darker display is taken to the away level too", bright.Hardware == 25);
            Native.Idle = 0;
            Pump(2000);

            // --- switched off, the old behaviour is exactly as it was ----------------------
            settings.SetAutoRestore(bright, false);
            Settle(engine, bright, 90);
            bright.Reads = 0;
            Native.Idle = 9000;
            Pump(2000);
            Check("without auto restore the display is read before dimming", bright.Reads > 0);
            Check("and dimmed to the away level", bright.Hardware == 25);

            Native.Idle = 0;
            Check("and put back to the brightness it actually had",
                WaitFor(delegate { return bright.Hardware == 90; }, 6000));

            settings.DisplayFallbacks.Clear();

            // --- the working set is handed back once nobody is at the desk -----------------
            // A tray application holds on to pages it will not touch again until somebody comes
            // back, and Windows reclaims those only under memory pressure - which is why the
            // figure in Task Manager climbs all evening and stays climbed. They are given up
            // once the user has actually gone, and never while they are sitting there.
            Settle(engine, bright, 100);
            Native.Idle = 0;
            Pump(1200);

            Native.Trims = 0;
            Pump(2000);
            Check("nothing is handed back while somebody is at the desk", Native.Trims == 0);

            Native.Idle = 9000;
            Pump(1800);
            Check("dimmed, ready for the trim", engine.State == DimState.Dimmed);
            Check("and nothing is handed back before the fade could have finished", Native.Trims == 0);

            Pump(6000);
            Check("the working set is handed back once the screen is dim", Native.Trims == 1);
            Pump(3000);
            Check("and handed back once, not on every tick", Native.Trims == 1);

            Native.Idle = 0;
            Pump(2000);

            // The screen being switched off is the longest stretch of doing nothing there is:
            // nothing is read, nothing is written, and nothing is decided until it comes back.
            Native.Trims = 0;
            engine.SetScreenOn(false);
            Check("and handed back when Windows switches the screen off", Native.Trims == 1);
            engine.SetScreenOn(true);
            Pump(1500);

            Settle(engine, bright, 100);
            settings.RestoreFallback = 100;
            Native.Idle = 9000;
            Pump(2000);

            // Shutdown always hands the hardware back
            Check("still dimmed before shutdown", engine.State == DimState.Dimmed);
            engine.ShutdownRestore();
            Check("shutdown restores even from an override", bright.Hardware == 100 && dim.Hardware == 20);

            // ... including while a check is still in flight. There is no later to wait for on
            // the way out: a check that has not finished holds every write back, so leaving it
            // set would put the machine to sleep with the displays still dimmed.
            engine.Start();
            Settle(engine, bright, 100);
            Native.Idle = 9000;
            Pump(1800);
            Check("dimmed before shutting down mid-check", bright.Hardware == 25);

            displays.AnswersAfter = 100;           // a check that will not finish on its own
            engine.SmartRestore();
            Pump(400);
            Check("a check is holding the screen at shutdown", engine.AwaitingRescan);

            engine.ShutdownRestore();
            Check("shutting down mid-check still hands the displays back", bright.Hardware == 100);
            displays.AnswersAfter = 0;

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : _failures + " CHECK(S) FAILED");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }

        /// <summary>
        /// Puts one display back to a known state between scenarios. Each of these tests leaves
        /// the engine watching over a restore it just made, and without a clean slate the next
        /// scenario inherits that and proves nothing.
        /// </summary>
        private static void Settle(DimEngine engine, DisplayTarget target, int hardware)
        {
            // Come back first: a scenario that starts already dimmed would find its own dim is
            // no change at all, so nothing would be written and nothing would be proved.
            Native.Idle = 0;
            Pump(1600);

            // Stop whatever the engine still has in flight before touching the display, not
            // after. A restore is watched over for several seconds, and a fade or a retry may
            // be part way through; any of them would write to this display after it had been
            // set up here, and the next scenario would start from a state nobody chose.
            // Asking for a fresh transition cancels all of it, and the pump lets the worker
            // notice before the slate is wiped.
            engine.Reapply();
            Pump(500);

            target.Rejects = null;
            target.Readable = true;
            target.Captured = null;
            target.Applied = null;
            target.Hardware = hardware;
        }

        private static void Check(string what, bool passed)
        {
            Console.WriteLine((passed ? "  pass  " : "  FAIL  ") + what);
            if (!passed) _failures++;
        }

        /// <summary>
        /// Pumps until something becomes true, or the time runs out. Anything that depends on
        /// the engine's own timing waits for the outcome rather than guessing how long it takes,
        /// which is the difference between a test and a coin toss.
        /// </summary>
        private static bool WaitFor(Func<bool> condition, int milliseconds)
        {
            int until = Environment.TickCount + milliseconds;
            while (Environment.TickCount < until)
            {
                if (condition()) return true;
                Application.DoEvents();
                DisplayManager.DrainPending();
                Thread.Sleep(50);
            }
            return condition();
        }

        /// <summary>Runs the message loop so the engine's timer ticks, without blocking it.</summary>
        private static void Pump(int milliseconds)
        {
            int until = Environment.TickCount + milliseconds;
            while (Environment.TickCount < until)
            {
                Application.DoEvents();
                DisplayManager.DrainPending();
                Thread.Sleep(15);
            }
        }
    }
}
