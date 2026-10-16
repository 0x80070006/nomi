using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace Nomi
{
    // User preferences, stored per user in %APPDATA%\Nomi\settings.ini.
    // Positions are in physical screen pixels; NaN means "not placed yet".
    class Settings
    {
        // Mascot
        public bool ShowMascot = true;
        public double MascotSize = 128;            // 64 to 320 (DIP)
        public double MascotOpacity = 1.0;         // 0.3 to 1
        public double MascotX = double.NaN;
        public double MascotY = double.NaN;
        public bool AlwaysOnTop = true;
        public bool Animations = true;
        public double SleepMinutes = 10;           // 0 = never

        // Assistant panel (the "bubble")
        public string PanelTheme = "dark";         // dark | light
        public string Accent = "#F5A623";
        public string PanelBackground = "";        // "" = theme default, or #RRGGBB
        public double PanelOpacity = 0.97;         // 0.4 to 1
        public double PanelWidth = 400;            // 300 to 900 (DIP)
        public double PanelHeight = 580;           // 320 to 1000 (DIP)
        public double FontSize = 13;               // 11 to 18
        public bool Rounded = true;
        public string PanelPlacement = "mascot";   // mascot | free
        public double PanelX = double.NaN;
        public double PanelY = double.NaN;

        // Assisted manual reply
        public string HighlightColor = "#F5A623";
        public double HighlightOpacity = 0.28;     // 0.1 to 0.8

        // Outlook
        public bool OutlookSuggestions = false;
        public double SuggestionCount = 5;         // 1 to 10
        public bool SuggestionsUnreadOnly = false;
        public bool AutoDrafts = false;            // also save each suggestion as an Outlook draft
        public string SignatureName = "";
        public string ReplyLanguage = "auto";      // auto | fr | en
        public string DefaultTone = "neutre";      // neutre | formel | chaleureux | bref

        // Local model
        public string ModelPath = "";              // "" = bundled model
        public double Threads = 0;                 // 0 = automatic
        public double ContextSize = 4096;
        public double MaxTokens = 450;
        public double Temperature = 0.3;
        public bool PreloadModel = true;
        public double UnloadAfterMinutes = 20;     // 0 = keep loaded

        // General
        public bool LaunchAtStartup = true;
        public bool Hotkeys = true;                // Ctrl+Alt+N panel, Ctrl+Alt+R zone

        public static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nomi");
        static readonly string FilePath = Path.Combine(Folder, "settings.ini");

        static FieldInfo[] Fields
        {
            get { return typeof(Settings).GetFields(BindingFlags.Public | BindingFlags.Instance); }
        }

        public Settings Clone() { return (Settings)MemberwiseClone(); }

        public void CopyFrom(Settings other)
        {
            foreach (var f in Fields) f.SetValue(this, f.GetValue(other));
        }

        public bool SameAs(Settings other)
        {
            return Fields.All(f => Equals(f.GetValue(this), f.GetValue(other)));
        }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                string path = File.Exists(FilePath) ? FilePath : File.Exists(FilePath + ".bak") ? FilePath + ".bak" : null;
                if (path == null) return s;
                foreach (string line in File.ReadAllLines(path))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().TrimStart('﻿');
                    var field = Fields.FirstOrDefault(f => f.Name == key);
                    if (field == null) continue;
                    string value = line.Substring(eq + 1).Trim();
                    if (field.FieldType == typeof(string)) field.SetValue(s, value);
                    else if (field.FieldType == typeof(bool)) field.SetValue(s, value == "true");
                    else if (field.FieldType == typeof(double))
                    {
                        double d;
                        if (value == "NaN") field.SetValue(s, double.NaN);
                        else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) field.SetValue(s, d);
                    }
                }
            }
            catch (Exception e) { Log.Error("Settings load", e); }
            s.Normalize();
            return s;
        }

        // Written to a temporary file first, so a crash never leaves a truncated configuration.
        public void Save()
        {
            Normalize();
            try
            {
                Directory.CreateDirectory(Folder);
                var lines = Fields.Select(f =>
                {
                    object v = f.GetValue(this);
                    string text = v is bool ? ((bool)v ? "true" : "false")
                                : v is double ? ((double)v).ToString("R", CultureInfo.InvariantCulture)
                                : (Convert.ToString(v) ?? "").Replace("\r", " ").Replace("\n", " ");
                    return f.Name + "=" + text;
                }).ToArray();
                string tmp = FilePath + ".tmp";
                File.WriteAllLines(tmp, lines);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, FilePath + ".bak", true);
                else File.Move(tmp, FilePath);
            }
            catch (Exception e) { Log.Error("Settings save", e); }
        }

        public void Normalize()
        {
            MascotSize = Clamp(MascotSize, 64, 320);
            MascotOpacity = Clamp(MascotOpacity, 0.3, 1);
            SleepMinutes = Clamp(SleepMinutes, 0, 240);
            PanelOpacity = Clamp(PanelOpacity, 0.4, 1);
            PanelWidth = Clamp(PanelWidth, 300, 900);
            PanelHeight = Clamp(PanelHeight, 320, 1000);
            FontSize = Clamp(FontSize, 11, 18);
            HighlightOpacity = Clamp(HighlightOpacity, 0.1, 0.8);
            SuggestionCount = Math.Round(Clamp(SuggestionCount, 1, 10));
            Threads = Math.Round(Clamp(Threads, 0, 64));
            ContextSize = Math.Round(Clamp(ContextSize, 2048, 32768));
            MaxTokens = Math.Round(Clamp(MaxTokens, 64, 2048));
            Temperature = Clamp(Temperature, 0, 1.5);
            UnloadAfterMinutes = Clamp(UnloadAfterMinutes, 0, 600);

            if (PanelTheme != "dark" && PanelTheme != "light") PanelTheme = "dark";
            if (PanelPlacement != "mascot" && PanelPlacement != "free") PanelPlacement = "mascot";
            if (new[] { "auto", "fr", "en" }.All(x => x != ReplyLanguage)) ReplyLanguage = "auto";
            if (new[] { "neutre", "formel", "chaleureux", "bref" }.All(x => x != DefaultTone)) DefaultTone = "neutre";
            if (!ColorUtil.IsHex(Accent)) Accent = "#F5A623";
            if (!ColorUtil.IsHex(HighlightColor)) HighlightColor = "#F5A623";
            if (PanelBackground != "" && !ColorUtil.IsHex(PanelBackground)) PanelBackground = "";
            if (SignatureName == null) SignatureName = "";
            if (ModelPath == null) ModelPath = "";
        }

        public static double Clamp(double v, double min, double max)
        {
            return double.IsNaN(v) ? min : Math.Max(min, Math.Min(max, v));
        }
    }

    // "Launch with Windows". The MSI registers the app for every user (HKLM Run, with --autostart);
    // turning it off is then a per-user setting checked at launch. Without the MSI entry the app
    // manages its own HKCU Run value.
    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "Nomi";

        public static void Apply(bool enabled)
        {
            bool machineWide = false;
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = hklm.OpenSubKey(RunKey))
                    machineWide = key != null && key.GetValue(ValueName) != null;
            }
            catch { }

            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (enabled && !machineWide)
                        key.SetValue(ValueName, "\"" + Assembly.GetExecutingAssembly().Location + "\" --autostart");
                    else
                        key.DeleteValue(ValueName, false);
                }
            }
            catch (Exception e) { Log.Error("Startup", e); }
        }
    }
}
