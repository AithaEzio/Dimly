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

        /// <summary>Ticks of silence to remember. One tick over the longest possible delay is
        /// enough for the countdown; capping it keeps the millisecond conversion in range.</summary>
        private const int MaxQuietTicks = AppSettings.MaxIdleSeconds + 1;

        private readonly AppSettings _settings;
        private readonly DisplayManager _displays;
        private readonly MediaWatcher _media;
        private readonly ActivityWatcher _activity;
        private readonly Timer _tick;

        private readonly object _queueGate = new object();
        private Task _queue = CompletedTask();
        private CancellationTokenSource _cancellation;

        private DimState _state = DimState.Awake;
        private bool _locked;
        private bool _paused;

        /// <summary>The manual override: dimmed because the user asked, and staying that way
        /// until the user says otherwise. Unlike an away dim, moving the mouse does not undo it.</summary>
        private bool _overridden;

        /// <summary>Ticks since sound was last heard. Zero means playback is holding the
        /// countdown at the start line; it begins counting the moment playback stops.</summary>
        private int _quietTicks = MaxQuietTicks;

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
            UpdateQuiet();
            Evaluate();
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
            RaiseChanged();
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

        /// <summary>Re-enumerates displays after a hardware change, restoring the old set first.</summary>
        public void ReloadDisplays()
        {
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
            public bool Dim;
            public int Level;
            public bool Fade;
            public int FadeMillis;
        }

        private Intent Snapshot()
        {
            Intent intent = new Intent();
            intent.Targets = _displays.Targets;
            intent.Disabled = new HashSet<string>(_settings.DisabledDisplays, StringComparer.OrdinalIgnoreCase);
            intent.Dim = _state == DimState.Dimmed;
            intent.Level = _settings.AwayBrightness;
            intent.Fade = _settings.Fade;
            intent.FadeMillis = _settings.FadeMillis;
            return intent;
        }

        private void Apply(bool immediate)
        {
            Intent intent = Snapshot();
            if (immediate) intent.Fade = false;
            Enqueue(delegate(CancellationToken token) { Transition(intent, token); });
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
                    if (target.Captured == null)
                    {
                        int current;
                        target.Captured = target.TryRead(out current) ? current : 100;
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
            foreach (Move move in moves)
            {
                if (!move.Release) continue;
                move.Target.Captured = null;
                move.Target.Applied = null;
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
