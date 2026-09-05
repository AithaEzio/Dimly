using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace Dimly
{
    public enum DimState
    {
        /// <summary>Someone is at the desk. Displays are at their own brightness.</summary>
        Awake,
        /// <summary>Nobody is at the desk. Displays are held at the away level.</summary>
        Dimmed
    }

    /// <summary>
    /// Decides when to dim and when to come back, and owns every brightness write.
    /// Decisions happen on the UI thread once a second; the writes - which block for tens of
    /// milliseconds per monitor - happen on a single background worker that can be interrupted
    /// mid-fade the instant the user touches the mouse.
    /// </summary>
    public sealed class DimEngine : IDisposable
    {
        private const int TickMilliseconds = 1000;

        /// <summary>How long a restored brightness is watched over. Long enough to outlast a
        /// monitor's power-on sequence, short enough not to fight the user's own buttons.</summary>
        private const int RestoreHoldMilliseconds = 10000;
        private const int RestoreCheckMilliseconds = 1500;
        private const int RestoreTolerance = 3;

        /// <summary>How often to offer a refused restore back to the display.</summary>
        private const int RestoreRetryMilliseconds = 2000;

        /// <summary>
        /// How long a monitor is left alone after Windows says the screen is on again. Windows
        /// reports the state it has asked for, not the state the hardware has reached: a
        /// monitor coming out of power save takes seconds to light up, and through those
        /// seconds it answers nothing and keeps nothing it is told. Nothing is even asked of it
        /// until this has passed.
        /// </summary>
        private const int DisplayWakeSettleMilliseconds = 3000;

        /// <summary>How often to ask whether the displays are answering, once settled.</summary>
        private const int DisplayWakePollMilliseconds = 400;

        /// <summary>
        /// The longest the screen is held dim waiting for a display to speak up. Past this the
        /// restore goes ahead anyway: a screen left dark is worse than a write nobody confirmed,
        /// and that write is read back, retried and watched over regardless.
        /// </summary>
        private const int DisplayWakeTimeoutMilliseconds = 15000;

        /// <summary>Ticks of silence to remember. One tick over the longest possible delay is
        /// enough for the countdown; capping it keeps the millisecond conversion in range.</summary>
        private const int MaxQuietTicks = AppSettings.MaxIdleSeconds + 1;

        /// <summary>
        /// How long after the screen dims before the working set is handed back. Longer than the
        /// longest fade, so the pages the fade is running out of are not taken away mid-write.
        /// </summary>
        private const int TrimDelayMilliseconds = 5000;

        private readonly AppSettings _settings;
        private readonly DisplayManager _displays;
        private readonly MediaWatcher _media;
        private readonly ActivityWatcher _activity;
        private readonly Timer _tick;

        private readonly object _queueGate = new object();
        private Task _queue = CompletedTask();
        private CancellationTokenSource _cancellation;
        private readonly Queue<Action> _reads = new Queue<Action>();

        private DimState _state = DimState.Awake;
        private bool _locked;
        private bool _paused;

        /// <summary>The manual override: dimmed because the user asked, and staying that way
        /// until the user says otherwise. Unlike an away dim, moving the mouse does not undo it.</summary>
        private bool _overridden;

        /// <summary>
        /// True while the displays are being looked over after the screen came back on. Nothing
        /// is decided and nothing is written until that finishes - the screen stays exactly
        /// where it is, even once the user is back - because a monitor still powering up
        /// answers with values that are worse than useless.
        /// </summary>
        private bool _awaitingRescan;

        /// <summary>
        /// Whether Windows has the screen switched on. It says so before it starts powering the
        /// monitors down, and while they are going down or dark they refuse everything - so
        /// nothing is written, nothing is read, and nothing is concluded until they are back.
        /// </summary>
        private bool _screenOn = true;

        /// <summary>When the current check began, so one that never ends can be noticed.</summary>
        private int _rescanStartedTick;

        /// <summary>Ticks since sound was last heard. Zero means playback is holding the
        /// countdown at the start line; it begins counting the moment playback stops.</summary>
        private int _quietTicks = MaxQuietTicks;
        /// <summary>
        /// When the next retry is due. Started at the clock rather than at zero: the tick count
        /// turns negative after 24.9 days of uptime, and zero is not a reading from that clock,
        /// so "now minus zero" would be negative for a whole 24.9-day stretch and hold every
        /// retry off for as long as the machine stayed up.
        /// </summary>
        private int _nextRetryTick = Environment.TickCount;

        /// <summary>A working-set trim is waiting for the dim to finish. See <see cref="TrimWhenIdle"/>.</summary>
        private bool _trimPending;
        private int _trimAtTick;

        public DimEngine(AppSettings settings, DisplayManager displays, MediaWatcher media, ActivityWatcher activity)
        {
            _settings = settings;
            _displays = displays;
            _media = media;
            _activity = activity;
            _tick = new Timer();
            _tick.Interval = TickMilliseconds;
            _tick.Tick += OnTick;
        }

        /// <summary>Raised on the UI thread whenever the state, pause or override flags change.</summary>
        public event EventHandler Changed;

        public DimState State { get { return _state; } }

        /// <summary>True while Smart restore is checking the displays over.</summary>
        public bool AwaitingRescan { get { return _awaitingRescan; } }

        /// <summary>
        /// Windows has told us whether the screen is on. While it is not, Dimly leaves the
        /// displays entirely alone: a monitor being powered down rejects every command, and
        /// acting on those rejections is how a working display gets written off as broken.
        /// </summary>
        public void SetScreenOn(bool on)
        {
            if (_screenOn == on) return;

            _screenOn = on;
            _displays.ScreenOn = on;

            // The screen going dark is the longest stretch of doing nothing there is: no reading,
            // no writing, and nothing decided until it comes back. Give the pages up now rather
            // than on the schedule below, which the dim may not have got round to yet.
            if (!on) { _trimPending = false; Native.TrimMemory(); }
        }

        /// <summary>Raised when the displays have been looked at again and may have changed.</summary>
        public event EventHandler DisplaysChanged;

        /// <summary>
        /// Dims on demand and holds there. Turning it off restores whatever brightness the
        /// displays had when it went on.
        /// </summary>
        public bool Overridden
        {
            get { return _overridden; }
            set
            {
                if (_overridden == value) return;
                _overridden = value;
                Evaluate();
                RaiseChanged();
            }
        }

        /// <summary>
        /// Seconds counted towards dimming. It is the smaller of "time since the last input"
        /// and "time since sound stopped", so either one being recent keeps it at zero.
        /// </summary>
        public int CountdownSeconds
        {
            get { return Math.Min(IdleMilliseconds() / 1000, _quietTicks); }
        }

        /// <summary>
        /// How long the machine has been left alone. Normally that is the system idle clock,
        /// but a device reporting on its own - a drifting gamepad is the usual culprit - pins
        /// that at zero for good. When asked to, take whichever clock has seen quiet for longer:
        /// the watcher only counts input a person actually produced.
        /// </summary>
        private int IdleMilliseconds()
        {
            int system = Native.IdleMilliseconds();
            if (!_settings.IgnoreNoisyDevices || !_activity.Available) return system;
            return Math.Max(system, _activity.IdleMilliseconds);
        }

        /// <summary>True when sound, rather than the user, is what is holding the countdown.</summary>
        public bool HeldByMedia { get { return _quietTicks == 0; } }

        public bool Paused
        {
            get { return _paused; }
            set
            {
                if (_paused == value) return;
                _paused = value;
                Evaluate();
                RaiseChanged();
            }
        }

        public void Start()
        {
            _tick.Start();
        }

        // ------------------------------------------------------------ decisions

        private void OnTick(object sender, EventArgs e)
        {
            ReleaseStuckRescan();
            UpdateQuiet();
            Evaluate();
            RetryUnfinishedRestore();
            TrimWhenIdle();
        }

        /// <summary>
        /// Hands the working set back to Windows once nobody is at the desk.
        ///
        /// Dimly spends nearly all of its life asleep between one-second ticks, holding on to
        /// pages it touched on the way in and will not touch again until somebody comes back.
        /// Windows only reclaims those under memory pressure, so left alone the figure in Task
        /// Manager climbs and stays there. They are given up a few seconds after the screen
        /// dims, which is the one moment nothing can be waiting on them - and they fault back
        /// in, from memory, well inside the tens of milliseconds a single brightness write
        /// already takes.
        /// </summary>
        private void TrimWhenIdle()
        {
            if (!_trimPending) return;
            if (unchecked(Environment.TickCount - _trimAtTick) < 0) return;

            _trimPending = false;
            Native.TrimMemory();
        }

        /// <summary>
        /// Lets go of a check that has outlived any honest reason to still be running. Holding
        /// the screen dim is only ever meant to last seconds; if something has gone wrong the
        /// screen must come back regardless, because a dark screen nobody can explain is worse
        /// than a restore that arrives late.
        /// </summary>
        private void ReleaseStuckRescan()
        {
            if (!_awaitingRescan) return;

            int spent = unchecked(Environment.TickCount - _rescanStartedTick);
            if (spent < DisplayWakeSettleMilliseconds + DisplayWakeTimeoutMilliseconds + 5000) return;

            FinishSmartRestore(false);
        }

        /// <summary>
        /// A restore the display would not take leaves its captured brightness on record rather
        /// than being thrown away. Keep offering it back: a monitor that was still powering on,
        /// or briefly unplugged, will accept it a few seconds later.
        /// </summary>
        private void RetryUnfinishedRestore()
        {
            if (!_screenOn) return;
            if (_state != DimState.Awake || _paused || _overridden) return;
            if (unchecked(Environment.TickCount - _nextRetryTick) < 0) return;

            bool pending = false;
            foreach (DisplayTarget target in _displays.Targets)
                if (target.Captured != null) { pending = true; break; }
            if (!pending) return;

            _nextRetryTick = unchecked(Environment.TickCount + RestoreRetryMilliseconds);
            Apply(true);
        }

        /// <summary>
        /// Tracks how long the machine has been silent. The counter only moves on the timer
        /// tick, never on the event-driven calls into Evaluate, so one tick really is one
        /// second. Sampling is switched off entirely whenever its answer could not matter.
        /// </summary>
        private void UpdateQuiet()
        {
            _activity.Enabled = _settings.IgnoreNoisyDevices;
            _activity.PollGamepads();

            _media.Enabled = _settings.HoldWhileAudioPlays && !_paused && !_overridden;

            if (_media.Enabled && _media.IsPlaying) _quietTicks = 0;
            else if (_quietTicks < MaxQuietTicks) _quietTicks++;
        }

        private void Evaluate()
        {
            // Smart restore is mid-check: hold everything exactly as it is, the dim included.
            if (_awaitingRescan) return;

            // The screen is off, or on its way off. Nothing decided, nothing written.
            if (!_screenOn) return;

            // Pausing means "leave my screens alone", which outranks a forgotten manual dim.
            if (_paused) { _overridden = false; SetState(DimState.Awake); return; }

            if (_overridden) { SetState(DimState.Dimmed); return; }

            // While the workstation is locked, idle time is meaningless: the user may have
            // pressed Win+L a moment ago. The session events drive the state instead.
            if (_locked)
            {
                if (_settings.DimOnLock) SetState(DimState.Dimmed);
                return;
            }

            // The countdown starts from whichever happened later, the last input or the last
            // sound: pausing a film gives back the full delay rather than dimming on the spot.
            int elapsed = Math.Min(IdleMilliseconds(), _quietTicks * TickMilliseconds);
            int threshold = _settings.IdleSeconds * 1000;

            if (_state == DimState.Awake)
            {
                if (elapsed < threshold) return;
                if (_settings.SkipFullscreen && Native.IsFullscreenAppActive()) return;
                SetState(DimState.Dimmed);
            }
            else if (elapsed < threshold)
            {
                SetState(DimState.Awake);
            }
        }

        private void SetState(DimState next)
        {
            if (_state == next) return;
            _state = next;
            Apply(false);

            // Dimming means the user has gone; coming back means they have not.
            _trimPending = next == DimState.Dimmed;
            if (_trimPending) _trimAtTick = unchecked(Environment.TickCount + TrimDelayMilliseconds);

            RaiseChanged();
        }

        /// <summary>
        /// The machine has woken. Every display handle taken before it slept is dead, so they
        /// are re-established before anything is asked of them.
        /// </summary>
        public void OnResume()
        {
            _locked = false;
            Evaluate();

            Intent intent = Snapshot();
            Enqueue(delegate(CancellationToken token)
            {
                // Through the manager, so every handle is re-taken against the screen as
                // Windows has it now rather than as it was before the machine slept.
                _displays.Reacquire();
                intent.Targets = _displays.Targets;

                if (!token.IsCancellationRequested) Transition(intent, token);
            });
        }

        /// <summary>
        /// Smart restore. The screen has just come back from being switched off, and rather
        /// than trusting handles and readings that a powering-up monitor will happily give and
        /// then contradict, the displays are looked over from scratch first. The dim is held
        /// through it - the user may already be moving the mouse - and let go of only once the
        /// check is done, so the brightness comes back once and correctly rather than twice.
        /// </summary>
        public void SmartRestore()
        {
            if (_awaitingRescan) return;

            _awaitingRescan = true;
            _rescanStartedTick = Environment.TickCount;
            RaiseChanged();

            Enqueue(delegate(CancellationToken token)
            {
                bool listChanged = false;
                try
                {
                    WaitForDisplaysToWake(token);
                    if (!token.IsCancellationRequested) listChanged = _displays.Reacquire();
                }
                finally
                {
                    // Whatever happened - finished, cancelled, or thrown - the hold is let go
                    // of. It stops the screen being restored, so a path that leaves it set is a
                    // path that leaves the screen dark for good.
                    _displays.OnUiThread(delegate { FinishSmartRestore(listChanged); });
                }
            });
        }

        /// <summary>
        /// Waits for the monitors to come out of power save. They are left alone first - asking
        /// a monitor that is still dark tells you only that it is still dark - and then asked,
        /// gently, until they answer. Anything the user does meanwhile cancels this along with
        /// the rest of the work, and a display that never answers is given up on rather than
        /// left holding the screen dark.
        /// </summary>
        private void WaitForDisplaysToWake(CancellationToken token)
        {
            if (!Rest(DisplayWakeSettleMilliseconds, token)) return;

            int deadline = unchecked(Environment.TickCount + DisplayWakeTimeoutMilliseconds);
            while (unchecked(Environment.TickCount - deadline) < 0)
            {
                if (_displays.AllAnswering()) return;
                if (!Rest(DisplayWakePollMilliseconds, token)) return;
            }
        }

        /// <summary>Sleeps in slices, so that anything the user does is noticed at once.</summary>
        private static bool Rest(int milliseconds, CancellationToken token)
        {
            for (int waited = 0; waited < milliseconds; waited += 100)
            {
                if (token.IsCancellationRequested) return false;
                Thread.Sleep(100);
            }
            return !token.IsCancellationRequested;
        }

        private void FinishSmartRestore(bool listChanged)
        {
            if (!_awaitingRescan) return;
            _awaitingRescan = false;

            if (listChanged)
            {
                EventHandler handler = DisplaysChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }

            // Whatever the user did while the check was running is decided now, and the level
            // Dimly last set is put back on the record either way - the handles are new, and
            // nothing has yet proved itself against the display as it is now.
            Evaluate();
            RestoreWhatWasSet(false);
            RaiseChanged();
        }

        public void OnDisplayPowerOn()
        {
            RestoreWhatWasSet(true);
        }

        /// <summary>
        /// Puts the level Dimly last set back on the record, so the ordinary restore - written,
        /// read back, retried and watched over - has to prove itself against the display as it
        /// is now rather than as it was before the screen went dark. Handles are taken again
        /// too, unless a check has just done that.
        /// </summary>
        private void RestoreWhatWasSet(bool takeHandlesAgain)
        {
            Intent intent = Snapshot();
            Enqueue(delegate(CancellationToken token)
            {
                if (takeHandlesAgain)
                {
                    // Handles are re-taken against freshly enumerated screens, and a rebuild
                    // may have replaced the targets outright, so the list is read again.
                    _displays.Reacquire();
                    intent.Targets = _displays.Targets;
                }

                foreach (DisplayTarget target in intent.Targets)
                {
                    if (intent.Dim) continue;
                    if (target.Captured != null) continue;
                    if (!target.LastAwakeLevel.HasValue) continue;

                    target.Captured = target.LastAwakeLevel.Value;
                    target.Applied = null;
                }

                if (!token.IsCancellationRequested) Transition(intent, token);
            });
        }

        /// <summary>Windows told us the session locked or unlocked.</summary>
        public void SetLocked(bool locked)
        {
            _locked = locked;
            Evaluate();
        }

        /// <summary>Re-applies the current intent after a settings change - a new away level,
        /// or a display the user just switched off.</summary>
        public void Reapply()
        {
            Apply(false);
        }

        /// <summary>
        /// Reads what each display is really set to, for showing in the window. It goes through
        /// the same queue as everything else so that the window and the engine are never talking
        /// to a monitor at the same moment. A display that is dimmed reports the level it will
        /// return to, which is the one the user thinks of as its brightness.
        /// </summary>
        public void ReadLevels(Action<Dictionary<string, int>> whenKnown)
        {
            IList<DisplayTarget> targets = _displays.Targets;
            EnqueueRead(delegate
            {
                Dictionary<string, int> levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (DisplayTarget target in targets)
                {
                    // What the display is set to now, which is what the page promises to show.
                    // Only if it will not say does the level Dimly took from it stand in.
                    int level;
                    if (!target.TryRead(out level))
                    {
                        if (!target.Captured.HasValue) continue;
                        level = target.Captured.Value;
                    }
                    levels[target.Key] = level;
                }
                whenKnown(levels);
            });
        }

        /// <summary>
        /// Applies a brightness the user set by hand. Reaching for the slider says they are here
        /// as plainly as moving the mouse, so any dim in progress is let go of first and the new
        /// level becomes the display's own - there is then nothing left to put back.
        /// </summary>
        public void SetBrightness(DisplayTarget target, int percent)
        {
            bool wasDimmed = _state == DimState.Dimmed || _overridden;
            _overridden = false;
            _state = DimState.Awake;
            if (wasDimmed) RaiseChanged();

            Intent intent = Snapshot();
            Enqueue(delegate(CancellationToken token)
            {
                // This display is now wherever the user put it, so it is not part of the restore.
                target.Captured = null;
                target.Applied = null;
                target.LastAwakeLevel = percent;
                try { target.Write(percent); }
                catch (Exception) { }

                // Any other display that was dimmed still needs handing back.
                if (!token.IsCancellationRequested) Transition(intent, token);
            });
        }

        /// <summary>Re-enumerates displays after a hardware change, restoring the old set first.</summary>
        public void ReloadDisplays()
        {
            // Re-probing while the monitors are dark finds nothing that answers, and every
            // display would be written off as uncontrollable. It waits for the screen.
            if (!_screenOn) return;

            // Smart restore is already looking the displays over, and Windows announces a
            // display change twice the moment the screen comes back. Rebuilding now would
            // cancel that check part way through and leave the screen held dark.
            if (_awaitingRescan) return;

            Intent before = Snapshot();
            before.Dim = false;
            before.Fade = false;

            Intent after = Snapshot();
            after.Fade = false;

            Enqueue(delegate(CancellationToken token)
            {
                Transition(before, CancellationToken.None);
                _displays.Refresh();
                after.Targets = _displays.Targets;
                if (!token.IsCancellationRequested) Transition(after, token);
            });
        }

        /// <summary>Puts every display back and waits for it, so the process can exit honestly.</summary>
        public void ShutdownRestore()
        {
            _tick.Stop();
            _overridden = false;
            _state = DimState.Awake;

            // A check in progress holds every write back, and on the way out there is no later
            // to wait for: whatever it was going to conclude, the displays have to be handed
            // back now or they are left dim with nothing running to put them right.
            _awaitingRescan = false;
            Apply(true);

            Task pending;
            lock (_queueGate) pending = _queue;
            try { pending.Wait(3000); }
            catch (AggregateException) { }
        }

        public void Dispose()
        {
            _tick.Dispose();
        }

        private void RaiseChanged()
        {
            EventHandler handler = Changed;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        // -------------------------------------------------------------- writing

        /// <summary>A settings snapshot taken on the UI thread, so the worker never reads live state.</summary>
        private sealed class Intent
        {
            public IList<DisplayTarget> Targets;
            public HashSet<string> Disabled;

            /// <summary>Displays that want their own brightness back, rather than a chosen level.</summary>
            public HashSet<string> Manual;
            public bool Dim;
            public int Level;
            public bool Fade;
            public int FadeMillis;
            public Dictionary<string, int> Fallbacks;
            public int DefaultFallback;

            /// <summary>Lets the worker run any waiting readings while it is otherwise idle.</summary>
            public Action Pump;

            /// <summary>Where this display should be put if its own level cannot be restored.</summary>
            public int FallbackFor(DisplayTarget target)
            {
                int level;
                return Fallbacks.TryGetValue(target.Key, out level) ? level : DefaultFallback;
            }

            /// <summary>True when this display is simply put back to the level chosen for it.</summary>
            public bool AutoRestoreFor(DisplayTarget target)
            {
                return !Manual.Contains(target.Key);
            }
        }

        private Intent Snapshot()
        {
            Intent intent = new Intent();
            intent.Targets = _displays.Targets;
            intent.Disabled = new HashSet<string>(_settings.DisabledDisplays, StringComparer.OrdinalIgnoreCase);
            intent.Manual = new HashSet<string>(_settings.ManualRestoreDisplays, StringComparer.OrdinalIgnoreCase);
            intent.Dim = _state == DimState.Dimmed;
            intent.Level = _settings.AwayBrightness;
            intent.Fade = _settings.Fade;
            intent.FadeMillis = _settings.FadeMillis;
            intent.Fallbacks = new Dictionary<string, int>(_settings.DisplayFallbacks, StringComparer.OrdinalIgnoreCase);
            intent.DefaultFallback = _settings.RestoreFallback;
            intent.Pump = DrainReads;
            return intent;
        }

        private void Apply(bool immediate)
        {
            // Writing to a monitor that Windows is powering down achieves nothing and risks
            // everything: the write fails, and a failure is how a display gets written off.
            if (!_screenOn) return;

            // Nor while the monitors are still waking. Windows calls the screen on seconds
            // before they can accept anything, and a write in that gap fails for reasons that
            // have nothing to do with the display.
            if (_awaitingRescan) return;

            Intent intent = Snapshot();
            if (immediate) intent.Fade = false;
            Enqueue(delegate(CancellationToken token) { Transition(intent, token); });
        }

        /// <summary>
        /// Queues work that must not disturb what is already running. Reading a level is not a
        /// change of intent, and cancelling on its behalf would abandon the very fade it was
        /// asked to report on - so it waits its turn instead, under the running job's token so
        /// that a genuine change still leaves it behind.
        /// </summary>
        private void EnqueueRead(Action work)
        {
            lock (_queueGate)
            {
                _reads.Enqueue(work);
                _queue = _queue.ContinueWith(delegate { DrainReads(); },
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }

        /// <summary>
        /// Runs whatever readings are waiting. Called both from the queue and from the long
        /// watch that follows a restore, which would otherwise keep a reading waiting ten
        /// seconds for an answer the page is showing as live. Either way it runs on the worker,
        /// so only one thread is ever talking to a display.
        ///
        /// Nothing here is cancelled. A reading changes nothing, and one abandoned half way
        /// leaves the page showing a level the display left behind long ago - which is exactly
        /// what a page labelled realtime must never do.
        /// </summary>
        private void DrainReads()
        {
            for (; ; )
            {
                Action work;
                lock (_queueGate)
                {
                    if (_reads.Count == 0) return;
                    work = _reads.Dequeue();
                }

                try { work(); }
                catch (Exception) { }
            }
        }

        /// <summary>
        /// Queues work on the single writer thread, cancelling whatever is in flight. A fade that
        /// is half done simply stops where it is; the next transition starts from that value.
        /// </summary>
        private void Enqueue(Action<CancellationToken> work)
        {
            lock (_queueGate)
            {
                if (_cancellation != null) _cancellation.Cancel();

                // Not disposed on purpose: nothing ever waits on the token's handle, so there is
                // no resource to release, and disposing would race with the next Cancel().
                CancellationTokenSource cancellation = new CancellationTokenSource();
                _cancellation = cancellation;

                _queue = _queue.ContinueWith(delegate
                {
                    try { work(cancellation.Token); }
                    catch (Exception) { }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }

        /// <summary>
        /// A level no display can be at, marking a move that has written nothing yet. A move
        /// whose start and end are the same would otherwise be taken for one already finished.
        /// </summary>
        private const int NothingWrittenYet = -1;

        private sealed class Move
        {
            public DisplayTarget Target;
            public int From;
            public int To;
            public int Last;
            public bool Release;
        }

        private static void Transition(Intent intent, CancellationToken token)
        {
            List<Move> moves = new List<Move>();

            foreach (DisplayTarget target in intent.Targets)
            {
                bool wanted = intent.Dim && !intent.Disabled.Contains(target.Key);
                if (wanted)
                {
                    bool automatic = intent.AutoRestoreFor(target);
                    if (target.Captured == null)
                    {
                        // Auto restore has already chosen where this display comes back to, so
                        // it is never asked how bright it is. Otherwise the reading is what it
                        // comes back to, and the fallback covers a display that will not say.
                        int current;
                        target.Captured = automatic || !target.TryRead(out current)
                            ? intent.FallbackFor(target)
                            : current;
                        target.LastAwakeLevel = target.Captured;
                    }

                    if (automatic)
                    {
                        // Where the screen is now was deliberately not asked, so there is no
                        // honest place to fade from - and guessing high would flash the screen
                        // brighter before dimming it. It goes straight to the away level.
                        if (target.Applied.HasValue && target.Applied.Value == intent.Level) continue;

                        Move straight = NewMove(target, intent.Level, intent.Level, false);
                        straight.Last = NothingWrittenYet;
                        moves.Add(straight);
                        continue;
                    }

                    // Never brighten on the way out: a screen already below the away level stays put.
                    int goal = Math.Min(target.Captured.Value, intent.Level);
                    int from = target.Applied.HasValue ? target.Applied.Value : target.Captured.Value;
                    if (from == goal) { target.Applied = goal; continue; }
                    moves.Add(NewMove(target, from, goal, false));
                }
                else if (target.Captured != null)
                {
                    int from = target.Applied.HasValue ? target.Applied.Value : target.Captured.Value;
                    moves.Add(NewMove(target, from, target.Captured.Value, true));
                }
            }

            if (moves.Count == 0) return;

            int steps = 1;
            if (intent.Fade)
            {
                int span = 0;
                foreach (Move move in moves) span = Math.Max(span, Math.Abs(move.To - move.From));
                // One step per percentage point, capped: DDC/CI writes are slow and a monitor
                // gains nothing from being told about a half-percent change.
                steps = Math.Max(1, Math.Min(24, span));
            }

            Stopwatch clock = Stopwatch.StartNew();
            for (int step = 1; step <= steps; step++)
            {
                if (token.IsCancellationRequested) return;

                double progress = (double)step / steps;
                double eased = progress * progress * (3.0 - 2.0 * progress);

                foreach (Move move in moves)
                {
                    int value = step == steps
                        ? move.To
                        : (int)Math.Round(move.From + (move.To - move.From) * eased);
                    if (value == move.Last) continue;
                    move.Last = value;
                    try
                    {
                        move.Target.Write(value);
                        move.Target.Applied = value;
                    }
                    catch (Exception) { }   // one stubborn display must not stall the others
                }

                if (step == steps) break;
                int due = (int)(intent.FadeMillis * (double)step / steps);
                int wait = due - (int)clock.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep(wait);
            }

            if (token.IsCancellationRequested) return;

            // Putting brightness back is the one write that has to be proved. A display that
            // slept hands out dead handles which accept commands and ignore them, and the old
            // code believed them - forgetting the real brightness and stranding the screen dim
            // for good. The captured level is only let go of once the display agrees with it.
            List<Move> restored = new List<Move>();
            foreach (Move move in moves)
            {
                if (!move.Release) continue;

                bool ok = move.Target.TryWriteVerified(move.To);
                int fallback = intent.FallbackFor(move.Target);
                if (!ok && fallback != move.To)
                    ok = move.Target.TryWriteVerified(fallback);

                if (!ok) continue;   // keep what we know, and try again next time round

                restored.Add(move);
                move.Target.LastAwakeLevel = move.To;
                move.Target.Captured = null;
                move.Target.Applied = null;
            }

            HoldRestored(restored, token, intent.Pump);
        }

        /// <summary>
        /// Keeps a just-restored brightness in place for a few seconds.
        ///
        /// A monitor woken from power-off accepts the restore, confirms it when read back, and
        /// then finishes its own start-up by reloading the brightness it had - quietly undoing
        /// the restore seconds later. Dimly would notice nothing, and the *next* dim would read
        /// the dimmed screen and capture that as the level to come back to, so the display was
        /// stuck dim for good: quitting and dimming by hand both "restored" it to dim.
        /// </summary>
        private static void HoldRestored(List<Move> restored, CancellationToken token,
                                         Action pump)
        {
            if (restored.Count == 0) return;

            int deadline = unchecked(Environment.TickCount + RestoreHoldMilliseconds);
            while (!token.IsCancellationRequested && unchecked(Environment.TickCount - deadline) < 0)
            {
                // Slices rather than one long sleep: anything the user does next cancels this,
                // and it should take effect straight away.
                for (int waited = 0; waited < RestoreCheckMilliseconds; waited += 100)
                {
                    if (token.IsCancellationRequested) return;
                    Thread.Sleep(100);
                    if (pump != null) pump();
                }

                foreach (Move move in restored)
                {
                    int actual;
                    if (!move.Target.TryRead(out actual)) continue;
                    if (Math.Abs(actual - move.To) <= RestoreTolerance) continue;
                    move.Target.TryWriteVerified(move.To);
                }
            }
        }

        private static Move NewMove(DisplayTarget target, int from, int to, bool release)
        {
            Move move = new Move();
            move.Target = target;
            move.From = from;
            move.To = to;
            move.Last = from;
            move.Release = release;
            return move;
        }

        private static Task CompletedTask()
        {
            TaskCompletionSource<bool> source = new TaskCompletionSource<bool>();
            source.SetResult(true);
            return source.Task;
        }
    }
}
