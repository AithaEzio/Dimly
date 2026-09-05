using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Dimly
{
    /// <summary>
    /// Answers one question: is this machine making sound right now?
    ///
    /// It asks the Windows audio engine directly, walking every active render session and
    /// reading its peak meter. That covers anything that plays audio - a video in a browser
    /// tab, VLC, PotPlayer, a music player - without knowing anything about those programs.
    /// Pausing playback stops the stream, which is exactly the signal we want.
    ///
    /// Two deliberate choices make the answer trustworthy:
    ///
    /// * A session must be <c>Active</c> *and* audible. Programs that hold the audio device
    ///   open feeding digital silence (chat apps, some games) are common, and treating those
    ///   as playback would mean the screen never dimmed again.
    /// * An audible moment counts for a few seconds afterwards, so the quiet beat between two
    ///   lines of dialogue does not read as "stopped".
    ///
    /// Sampling happens on a pool thread, never on the UI thread: a stalled audio driver must
    /// not be able to hitch the window.
    /// </summary>
    public sealed class MediaWatcher : IDisposable
    {
        // Twice the rate of the engine's tick would be wasted work: the grace window below is
        // what decides how quickly a stop is noticed, not how often we look.
        private const int SampleMilliseconds = 2000;

        /// <summary>Below this, the stream is digital silence rather than quiet content.</summary>
        private const float AudibleThreshold = 0.0005f;

        /// <summary>How long an audible moment keeps counting as "still playing".</summary>
        private const int GraceMilliseconds = 6000;

        private readonly Timer _timer;

        /// <summary>Kept between samples: creating it costs far more than using it.</summary>
        private IMMDeviceEnumerator _enumerator;

        private int _lastAudibleTick;
        private int _sampling;
        private bool _enabled;
        private volatile bool _playing;

        public MediaWatcher()
        {
            // Start outside the grace window, so silence at startup is not mistaken for playback.
            _lastAudibleTick = unchecked(Environment.TickCount - GraceMilliseconds - 1);
            _timer = new Timer(Sample, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Whether to sample at all. While off, the timer is stopped and nothing is asked of
        /// the audio engine, so the feature costs literally nothing when it is not wanted.
        /// </summary>
        public bool Enabled
        {
            get { return _enabled; }
            set
            {
                if (_enabled == value) return;
                _enabled = value;

                if (value)
                {
                    _timer.Change(0, SampleMilliseconds);
                }
                else
                {
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
                    _playing = false;
                }
            }
        }

        /// <summary>True while sound has been coming out of the machine recently.</summary>
        public bool IsPlaying { get { return _playing; } }

        /// <summary>
        /// The loudest session peak seen in the last sample. Only used for diagnosing why a
        /// particular player is or is not being noticed - see tools/audioprobe.cs.
        /// </summary>
        public float LastPeak { get; private set; }

        private void Sample(object state)
        {
            // A slow audio driver must not let callbacks pile up on top of each other.
            if (Interlocked.Exchange(ref _sampling, 1) == 1) return;
            try
            {
                float peak = LoudestActiveSession();
                LastPeak = peak;

                int now = Environment.TickCount;
                if (peak >= AudibleThreshold) _lastAudibleTick = now;
                _playing = unchecked(now - _lastAudibleTick) < GraceMilliseconds;
            }
            catch (Exception)
            {
                // No audio endpoint, or COM refused. Silence is the safe answer: it lets the
                // screen dim rather than holding it bright forever on a machine we misread.
                _playing = false;
                DropEnumerator();
            }
            finally
            {
                Interlocked.Exchange(ref _sampling, 0);
            }
        }

        public void Dispose()
        {
            _timer.Dispose();
            DropEnumerator();
        }

        private void DropEnumerator()
        {
            IMMDeviceEnumerator stale = Interlocked.Exchange(ref _enumerator, null);
            Release(stale);
        }

        // ------------------------------------------------------- the audio engine

        private float LoudestActiveSession()
        {
            IMMDeviceCollection devices = null;
            float loudest = 0f;

            try
            {
                // The enumerator is free-threaded, so the pool thread of the moment can reuse
                // the one the last sample made. Only the endpoint list is asked for afresh,
                // which is what picks up a headset being plugged in.
                if (_enumerator == null) _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                if (_enumerator.EnumAudioEndpoints(DataFlowRender, DeviceStateActive, out devices) != 0) return 0f;

                uint count;
                if (devices.GetCount(out count) != 0) return 0f;

                // Every active output, not just the default one: sound may be going to a
                // headset while the speakers are still the default endpoint.
                for (uint i = 0; i < count; i++)
                {
                    IMMDevice device = null;
                    try
                    {
                        if (devices.Item(i, out device) != 0) continue;
                        float peak = LoudestOn(device);
                        if (peak > loudest) loudest = peak;
                    }
                    finally { Release(device); }
                }
            }
            finally
            {
                Release(devices);
            }

            return loudest;
        }

        /// <summary>Looked up once. Type.GUID reads an attribute back through reflection, and
        /// this is asked for once per audio endpoint on every single sample.</summary>
        private static readonly Guid AudioSessionManagerId = typeof(IAudioSessionManager2).GUID;

        private static float LoudestOn(IMMDevice device)
        {
            object managerObject = null;
            IAudioSessionEnumerator sessions = null;
            float loudest = 0f;

            try
            {
                Guid managerId = AudioSessionManagerId;
                if (device.Activate(ref managerId, ClsCtxAll, IntPtr.Zero, out managerObject) != 0) return 0f;

                IAudioSessionManager2 manager = (IAudioSessionManager2)managerObject;
                if (manager.GetSessionEnumerator(out sessions) != 0) return 0f;

                int count;
                if (sessions.GetCount(out count) != 0) return 0f;

                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl session = null;
                    try
                    {
                        if (sessions.GetSession(i, out session) != 0) continue;

                        // Checking the state first skips the long tail of expired sessions
                        // that every machine accumulates, and their meter queries with them.
                        int sessionState;
                        if (session.GetState(out sessionState) != 0 || sessionState != SessionStateActive) continue;

                        IAudioMeterInformation meter = session as IAudioMeterInformation;
                        if (meter == null) continue;

                        float peak;
                        if (meter.GetPeakValue(out peak) == 0 && peak > loudest) loudest = peak;
                    }
                    finally { Release(session); }
                }
            }
            finally
            {
                Release(sessions);
                Release(managerObject);
            }

            return loudest;
        }

        /// <summary>These objects are created once a second; waiting for a finaliser is not good enough.</summary>
        private static void Release(object instance)
        {
            if (instance != null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
        }

        // ------------------------------------------------------------- COM surface
        //
        // Only the methods Dimly calls are declared, but every one that precedes them must be
        // present: these are raw vtable layouts, so a missing entry silently calls the wrong
        // function. Each interface below is truncated exactly at the last method used.

        private const int DataFlowRender = 0;
        private const int DeviceStateActive = 0x00000001;
        private const int SessionStateActive = 1;
        private const uint ClsCtxAll = 23;

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int Item(uint index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid interfaceId, uint classContext, IntPtr activationParameters,
                [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            // Inherited from IAudioSessionManager - never called, but they hold vtable slots 0 and 1.
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionId, int streamFlags, out IAudioSessionControl session);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionId, int streamFlags, out IntPtr volume);

            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, out IAudioSessionControl session);
        }

        [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl
        {
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioMeterInformation
        {
            [PreserveSig] int GetPeakValue(out float peak);
        }
    }
}
