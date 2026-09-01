using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace Dimly
{
    /// <summary>
    /// Everything the user can change, persisted as a small INI next to the roaming profile.
    /// The executable stays a single portable file; only this text file is left behind.
    /// </summary>
    public sealed class AppSettings
    {
        public const int MinIdleSeconds = 5;
        public const int MaxIdleSeconds = 1800;

        public int AwayBrightness { get; set; }
        public int IdleSeconds { get; set; }
        public bool Fade { get; set; }
        public int FadeMillis { get; set; }
        public bool DimOnLock { get; set; }
        public bool SkipFullscreen { get; set; }

        /// <summary>Hold the countdown while the machine is making sound.</summary>
        public bool HoldWhileAudioPlays { get; set; }

        /// <summary>Count real keyboard, mouse and gamepad use instead of trusting the system
        /// idle clock, which any self-reporting HID device can pin at zero.</summary>
        public bool IgnoreNoisyDevices { get; set; }
        public bool StartHidden { get; set; }
        public string ThemeId { get; set; }

        /// <summary>Display keys the user has opted out of. Unknown keys are kept so that
        /// unplugging a monitor does not silently re-enable it.</summary>
        public HashSet<string> DisabledDisplays { get; private set; }

        public AppSettings()
        {
            AwayBrightness = 20;
            IdleSeconds = 60;
            Fade = true;
            FadeMillis = 700;
            DimOnLock = true;
            SkipFullscreen = true;
            HoldWhileAudioPlays = true;
            IgnoreNoisyDevices = false;
            StartHidden = false;
            ThemeId = "midnight";
            DisabledDisplays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public bool IsEnabled(DisplayTarget display)
        {
            return !DisabledDisplays.Contains(display.Key);
        }

        public void SetEnabled(DisplayTarget display, bool enabled)
        {
            if (enabled) DisabledDisplays.Remove(display.Key);
            else DisabledDisplays.Add(display.Key);
        }

        // ------------------------------------------------------------ storage

        public static string FilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dimly");
                return Path.Combine(dir, "settings.ini");
            }
        }

        /// <summary>The same location written for people to read: no expanded user name,
        /// which matters because this string is on screen and ends up in screenshots.</summary>
        public static string DisplayPath
        {
            get { return @"%AppData%\Dimly\settings.ini"; }
        }

        public static AppSettings Load()
        {
            AppSettings settings = new AppSettings();
            string path = FilePath;
            if (!File.Exists(path)) return settings;

            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == '[') continue;

                    int split = line.IndexOf('=');
                    if (split <= 0) continue;

                    string key = line.Substring(0, split).Trim();
                    string value = line.Substring(split + 1).Trim();
                    settings.Assign(key, value);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            settings.Clamp();
            return settings;
        }

        private void Assign(string key, string value)
        {
            switch (key)
            {
                case "AwayBrightness": AwayBrightness = ParseInt(value, AwayBrightness); break;
                case "IdleSeconds": IdleSeconds = ParseInt(value, IdleSeconds); break;
                case "Fade": Fade = ParseBool(value, Fade); break;
                case "FadeMillis": FadeMillis = ParseInt(value, FadeMillis); break;
                case "DimOnLock": DimOnLock = ParseBool(value, DimOnLock); break;
                case "SkipFullscreen": SkipFullscreen = ParseBool(value, SkipFullscreen); break;
                case "HoldWhileAudioPlays": HoldWhileAudioPlays = ParseBool(value, HoldWhileAudioPlays); break;
                case "IgnoreNoisyDevices": IgnoreNoisyDevices = ParseBool(value, IgnoreNoisyDevices); break;
                case "StartHidden": StartHidden = ParseBool(value, StartHidden); break;
                case "Theme": ThemeId = value; break;
                case "DisabledDisplays":
                    foreach (string entry in value.Split('|'))
                        if (entry.Length > 0) DisabledDisplays.Add(entry);
                    break;
            }
        }

        private void Clamp()
        {
            AwayBrightness = Math.Max(0, Math.Min(100, AwayBrightness));
            IdleSeconds = Math.Max(MinIdleSeconds, Math.Min(MaxIdleSeconds, IdleSeconds));
            FadeMillis = Math.Max(100, Math.Min(4000, FadeMillis));
            if (Theme.Find(ThemeId) == null) ThemeId = "midnight";
        }

        public void Save()
        {
            string path = FilePath;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                StringBuilder text = new StringBuilder();
                text.AppendLine("# Dimly settings - edited by the app, safe to delete.");
                Write(text, "AwayBrightness", AwayBrightness);
                Write(text, "IdleSeconds", IdleSeconds);
                Write(text, "Fade", Fade);
                Write(text, "FadeMillis", FadeMillis);
                Write(text, "DimOnLock", DimOnLock);
                Write(text, "SkipFullscreen", SkipFullscreen);
                Write(text, "HoldWhileAudioPlays", HoldWhileAudioPlays);
                Write(text, "IgnoreNoisyDevices", IgnoreNoisyDevices);
                Write(text, "StartHidden", StartHidden);
                text.AppendLine("Theme=" + ThemeId);

                string[] disabled = new string[DisabledDisplays.Count];
                DisabledDisplays.CopyTo(disabled);
                text.AppendLine("DisabledDisplays=" + string.Join("|", disabled));

                File.WriteAllText(path, text.ToString(), Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void Write(StringBuilder text, string key, int value)
        {
            text.AppendLine(key + "=" + value.ToString(CultureInfo.InvariantCulture));
        }

        private static void Write(StringBuilder text, string key, bool value)
        {
            text.AppendLine(key + "=" + (value ? "1" : "0"));
        }

        private static int ParseInt(string value, int fallback)
        {
            int parsed;
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : fallback;
        }

        private static bool ParseBool(string value, bool fallback)
        {
            if (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }
    }

    /// <summary>The "start with Windows" checkbox, backed directly by the Run key so that
    /// what the UI shows is always what Windows will actually do.</summary>
    public static class Startup
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Dimly";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return key != null && key.GetValue(ValueName) != null;
            }
            catch (Exception) { return false; }
        }

        public static void SetEnabled(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return;
                    if (enabled) key.SetValue(ValueName, "\"" + AppInfo.ExecutablePath + "\" --tray");
                    else key.DeleteValue(ValueName, false);
                }
            }
            catch (Exception) { }
        }
    }
}
