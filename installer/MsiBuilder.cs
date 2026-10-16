// Builds the Windows Installer package straight through msi.dll, so no WiX or other toolset is
// needed: everything used here ships with Windows. The whole staged folder (app, engine, model,
// licenses) goes into cabinets embedded in the .msi, so a single file installs everything offline.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

static class MsiBuilder
{
    const string UpgradeCode = "{5B1E7C2A-9D34-4F6B-A8E1-3C7D2F9A6B40}";
    const string ProductName = "Nomi";
    const string Manufacturer = "Nomi";
    const string ExeName = "Nomi.exe";

    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiOpenDatabase(string path, IntPtr persist, out IntPtr db);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiDatabaseOpenView(IntPtr db, string query, out IntPtr view);
    [DllImport("msi.dll")] static extern uint MsiViewExecute(IntPtr view, IntPtr record);
    [DllImport("msi.dll")] static extern uint MsiViewClose(IntPtr view);
    [DllImport("msi.dll")] static extern uint MsiCloseHandle(IntPtr handle);
    [DllImport("msi.dll")] static extern IntPtr MsiCreateRecord(uint fields);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiRecordSetString(IntPtr record, uint field, string value);
    [DllImport("msi.dll")] static extern uint MsiRecordSetInteger(IntPtr record, uint field, int value);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiRecordSetStream(IntPtr record, uint field, string path);
    [DllImport("msi.dll")] static extern uint MsiDatabaseCommit(IntPtr db);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiGetSummaryInformation(IntPtr db, string path, uint updateCount, out IntPtr summary);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiSummaryInfoSetProperty(IntPtr summary, uint property, uint dataType, int value, ref FILETIME time, string text);
    [DllImport("msi.dll")] static extern uint MsiSummaryInfoPersist(IntPtr summary);
    [DllImport("msi.dll", CharSet = CharSet.Unicode)] static extern uint MsiGetFileVersion(string path, StringBuilder version, ref uint versionLength, StringBuilder lang, ref uint langLength);

    class FileData { public string Path; public FileData(string p) { Path = p; } }

    class Entry
    {
        public string Source, Relative, Key, Component, Directory, Version, Language;
        public long Size;
        public bool Large;   // goes into the uncompressed cabinet (the model)
        public int Sequence;
    }

    static IntPtr db;

    public static int Main(string[] args)
    {
        if (args.Length != 4)
        {
            Console.Error.WriteLine("usage: MsiBuilder <stage-folder> <app.ico> <output.msi> <workdir>");
            return 2;
        }
        try { Build(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3])); return 0; }
        catch (Exception e) { Console.Error.WriteLine("error: " + e.Message); return 1; }
    }

    static void Build(string stage, string icon, string msi, string work)
    {
        string mainExe = Path.Combine(stage, ExeName);
        if (!File.Exists(mainExe)) throw new Exception(ExeName + " missing from " + stage);
        string fileVersion = GetFileVersion(mainExe);
        string productVersion = string.Join(".", fileVersion.Split('.').Take(3));

        // Files: small ones first (compressed cabinet), then the model (stored as is).
        var entries = new List<Entry>();
        foreach (string f in Directory.GetFiles(stage, "*", SearchOption.AllDirectories))
        {
            string rel = f.Substring(stage.Length).TrimStart('\\');
            var e = new Entry { Source = f, Relative = rel, Size = new FileInfo(f).Length };
            e.Large = e.Size > 200L * 1024 * 1024;
            if (e.Size > int.MaxValue) throw new Exception("file too large for MSI: " + rel);
            string dir = Path.GetDirectoryName(rel);
            e.Directory = string.IsNullOrEmpty(dir) ? "INSTALLFOLDER" : "DIR_" + Id(dir);
            e.Key = rel.Equals(ExeName, StringComparison.OrdinalIgnoreCase) ? "AppExe" : "F_" + Id(rel);
            e.Component = "C_" + Id(rel);
            string v = TryGetFileVersion(f);
            if (v != null) { e.Version = v; e.Language = "0"; }
            entries.Add(e);
        }
        entries = entries.OrderBy(e => e.Large).ThenBy(e => e.Relative, StringComparer.OrdinalIgnoreCase).ToList();
        for (int i = 0; i < entries.Count; i++) entries[i].Sequence = i + 1;
        int lastSmall = entries.Where(e => !e.Large).Select(e => e.Sequence).DefaultIfEmpty(0).Max();
        bool hasLarge = entries.Any(e => e.Large);

        string cabDir = Path.Combine(work, "cab");
        if (Directory.Exists(cabDir)) Directory.Delete(cabDir, true);
        Directory.CreateDirectory(cabDir);
        string smallCab = MakeCabinet(entries.Where(e => !e.Large).ToList(), cabDir, "app.cab", true);
        string largeCab = hasLarge ? MakeCabinet(entries.Where(e => e.Large).ToList(), cabDir, "model.cab", false) : null;

        if (File.Exists(msi)) File.Delete(msi);
        Check(MsiOpenDatabase(msi, (IntPtr)3 /* MSIDBOPEN_CREATE */, out db), "MsiOpenDatabase");
        try
        {
            CreateTables();

            Insert("Property", "Property,Value", "ProductCode", NewGuid());
            Insert("Property", "Property,Value", "ProductName", ProductName);
            Insert("Property", "Property,Value", "ProductVersion", productVersion);
            Insert("Property", "Property,Value", "ProductLanguage", "1036");
            Insert("Property", "Property,Value", "Manufacturer", Manufacturer);
            Insert("Property", "Property,Value", "UpgradeCode", UpgradeCode);
            Insert("Property", "Property,Value", "ALLUSERS", "1");
            Insert("Property", "Property,Value", "ARPPRODUCTICON", "AppIcon.ico");
            Insert("Property", "Property,Value", "ARPNOMODIFY", "1");
            Insert("Property", "Property,Value", "ARPNOREPAIR", "1");
            Insert("Property", "Property,Value", "ARPCOMMENTS", "Assistant de rédaction local pour Outlook");
            Insert("Property", "Property,Value", "SecureCustomProperties", "PREVIOUSVERSIONSINSTALLED");

            Insert("Directory", "Directory,Directory_Parent,DefaultDir", "TARGETDIR", null, "SourceDir");
            Insert("Directory", "Directory,Directory_Parent,DefaultDir", "ProgramFiles64Folder", "TARGETDIR", ".");
            Insert("Directory", "Directory,Directory_Parent,DefaultDir", "INSTALLFOLDER", "ProgramFiles64Folder", "Nomi");
            Insert("Directory", "Directory,Directory_Parent,DefaultDir", "ProgramMenuFolder", "TARGETDIR", ".");
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in entries)
            {
                string dir = Path.GetDirectoryName(e.Relative);
                if (string.IsNullOrEmpty(dir) || !dirs.Add(dir)) continue;
                if (dir.Contains("\\")) throw new Exception("only one level of sub-folders is supported: " + dir);
                Insert("Directory", "Directory,Directory_Parent,DefaultDir", "DIR_" + Id(dir), "INSTALLFOLDER", ShortName(dir, 0, true) + "|" + dir);
            }

            Insert("Feature", "Feature,Feature_Parent,Title,Description,Display,Level,Directory_,Attributes",
                "MainFeature", null, ProductName, "Nomi, son moteur et son modèle local", 1, 1, "INSTALLFOLDER", 0);

            int n = 0;
            foreach (var e in entries)
            {
                Insert("Component", "Component,ComponentId,Directory_,Attributes,Condition,KeyPath",
                    e.Component, StableGuid(e.Relative), e.Directory, 256 /* 64-bit */, null, e.Key);
                Insert("File", "File,Component_,FileName,FileSize,Version,Language,Attributes,Sequence",
                    e.Key, e.Component, ShortName(Path.GetFileName(e.Relative), ++n, false) + "|" + Path.GetFileName(e.Relative),
                    (int)e.Size, e.Version, e.Language, 0, e.Sequence);
                Insert("FeatureComponents", "Feature_,Component_", "MainFeature", e.Component);
            }

            Insert("Media", "DiskId,LastSequence,DiskPrompt,Cabinet,VolumeLabel,Source", 1, lastSmall, null, "#app.cab", null, null);
            Insert("_Streams", "Name,Data", "app.cab", new FileData(smallCab));
            if (hasLarge)
            {
                Insert("Media", "DiskId,LastSequence,DiskPrompt,Cabinet,VolumeLabel,Source", 2, entries.Count, null, "#model.cab", null, null);
                Insert("_Streams", "Name,Data", "model.cab", new FileData(largeCab));
            }

            // Start with Windows for every user; each user can turn it off in the app settings.
            Insert("Registry", "Registry,Root,Key,Name,Value,Component_",
                "AutoStart", 2, @"Software\Microsoft\Windows\CurrentVersion\Run", "Nomi", "\"[#AppExe]\" --autostart", "C_" + Id(ExeName));

            Insert("Shortcut", "Shortcut,Directory_,Name,Component_,Target,Arguments,Description,Hotkey,Icon_,IconIndex,ShowCmd,WkDir",
                "StartMenu", "ProgramMenuFolder", "Nomi", "C_" + Id(ExeName), "[#AppExe]", null,
                "Assistant de rédaction local pour Outlook", null, null, null, null, "INSTALLFOLDER");
            Insert("Shortcut", "Shortcut,Directory_,Name,Component_,Target,Arguments,Description,Hotkey,Icon_,IconIndex,ShowCmd,WkDir",
                "StartMenuSettings", "ProgramMenuFolder", "NOMIPA~1|Nomi - Paramètres", "C_" + Id(ExeName), "[#AppExe]", "--settings",
                "Paramètres de Nomi", null, null, null, null, "INSTALLFOLDER");

            Insert("Icon", "Name,Data", "AppIcon.ico", new FileData(icon));

            // Major upgrade: any earlier or identical version is removed first.
            Insert("Upgrade", "UpgradeCode,VersionMin,VersionMax,Language,Attributes,Remove,ActionProperty",
                UpgradeCode, "0.0.0", productVersion, null, 256 | 512 | 1, null, "PREVIOUSVERSIONSINSTALLED");

            // 34 = run exe in a directory, 64 = ignore exit code.
            Insert("CustomAction", "Action,Type,Source,Target",
                "StopRunningApp", 34 + 64, "INSTALLFOLDER", "\"[INSTALLFOLDER]" + ExeName + "\" --quit");
            // Launch through the shell so the app runs as the signed-in user, never elevated
            // (an elevated process cannot reach a normal Outlook over COM).
            Insert("CustomAction", "Action,Type,Source,Target",
                "LaunchApp", 34 + 64, "INSTALLFOLDER", "\"[WindowsFolder]explorer.exe\" \"[#AppExe]\"");

            object[][] execute =
            {
                new object[] { "FindRelatedProducts", null, 25 },
                new object[] { "CostInitialize", null, 800 },
                new object[] { "FileCost", null, 900 },
                new object[] { "CostFinalize", null, 1000 },
                new object[] { "StopRunningApp", "REMOVE~=\"ALL\" OR PREVIOUSVERSIONSINSTALLED", 1390 },
                new object[] { "InstallValidate", null, 1400 },
                new object[] { "RemoveExistingProducts", null, 1401 },
                new object[] { "InstallInitialize", null, 1500 },
                new object[] { "ProcessComponents", null, 1600 },
                new object[] { "UnpublishFeatures", null, 1800 },
                new object[] { "RemoveRegistryValues", null, 2600 },
                new object[] { "RemoveShortcuts", null, 3200 },
                new object[] { "RemoveFiles", null, 3500 },
                new object[] { "InstallFiles", null, 4000 },
                new object[] { "CreateShortcuts", null, 4500 },
                new object[] { "WriteRegistryValues", null, 5000 },
                new object[] { "RegisterUser", null, 6000 },
                new object[] { "RegisterProduct", null, 6100 },
                new object[] { "PublishFeatures", null, 6300 },
                new object[] { "PublishProduct", null, 6400 },
                new object[] { "InstallFinalize", null, 6600 },
                new object[] { "LaunchApp", "NOT REMOVE AND NOT NOMI_NOLAUNCH", 6700 }
            };
            foreach (var row in execute) Insert("InstallExecuteSequence", "Action,Condition,Sequence", row);

            object[][] ui =
            {
                new object[] { "FindRelatedProducts", null, 25 },
                new object[] { "CostInitialize", null, 800 },
                new object[] { "FileCost", null, 900 },
                new object[] { "CostFinalize", null, 1000 },
                new object[] { "ExecuteAction", null, 1300 }
            };
            foreach (var row in ui) Insert("InstallUISequence", "Action,Condition,Sequence", row);

            WriteSummary();
            Check(MsiDatabaseCommit(db), "MsiDatabaseCommit");
        }
        finally { MsiCloseHandle(db); }

        Console.WriteLine("MSI: " + msi + "  (version " + productVersion + ", " + entries.Count + " files, " +
                          (new FileInfo(msi).Length / (1024 * 1024)) + " Mo)");
    }

    static void CreateTables()
    {
        string[] tables =
        {
            "CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` LONGCHAR NOT NULL LOCALIZABLE PRIMARY KEY `Property`)",
            "CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Directory`)",
            "CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38), `Directory_` CHAR(72) NOT NULL, `Attributes` SHORT NOT NULL, `Condition` CHAR(255), `KeyPath` CHAR(72) PRIMARY KEY `Component`)",
            "CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL LOCALIZABLE, `FileSize` LONG NOT NULL, `Version` CHAR(72), `Language` CHAR(20), `Attributes` SHORT, `Sequence` LONG NOT NULL PRIMARY KEY `File`)",
            "CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `LastSequence` LONG NOT NULL, `DiskPrompt` CHAR(64) LOCALIZABLE, `Cabinet` CHAR(255), `VolumeLabel` CHAR(32), `Source` CHAR(72) PRIMARY KEY `DiskId`)",
            "CREATE TABLE `Feature` (`Feature` CHAR(38) NOT NULL, `Feature_Parent` CHAR(38), `Title` CHAR(64) LOCALIZABLE, `Description` CHAR(255) LOCALIZABLE, `Display` SHORT, `Level` SHORT NOT NULL, `Directory_` CHAR(72), `Attributes` SHORT NOT NULL PRIMARY KEY `Feature`)",
            "CREATE TABLE `FeatureComponents` (`Feature_` CHAR(38) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Feature_`, `Component_`)",
            "CREATE TABLE `Registry` (`Registry` CHAR(72) NOT NULL, `Root` SHORT NOT NULL, `Key` CHAR(255) NOT NULL LOCALIZABLE, `Name` CHAR(255) LOCALIZABLE, `Value` LONGCHAR LOCALIZABLE, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Registry`)",
            "CREATE TABLE `Shortcut` (`Shortcut` CHAR(72) NOT NULL, `Directory_` CHAR(72) NOT NULL, `Name` CHAR(128) NOT NULL LOCALIZABLE, `Component_` CHAR(72) NOT NULL, `Target` CHAR(72) NOT NULL, `Arguments` CHAR(255), `Description` CHAR(255) LOCALIZABLE, `Hotkey` SHORT, `Icon_` CHAR(72), `IconIndex` SHORT, `ShowCmd` SHORT, `WkDir` CHAR(72) PRIMARY KEY `Shortcut`)",
            "CREATE TABLE `Icon` (`Name` CHAR(72) NOT NULL, `Data` OBJECT NOT NULL PRIMARY KEY `Name`)",
            "CREATE TABLE `Upgrade` (`UpgradeCode` CHAR(38) NOT NULL, `VersionMin` CHAR(20), `VersionMax` CHAR(20), `Language` CHAR(255), `Attributes` LONG NOT NULL, `Remove` CHAR(255), `ActionProperty` CHAR(72) NOT NULL PRIMARY KEY `UpgradeCode`, `VersionMin`, `VersionMax`, `Language`, `Attributes`)",
            "CREATE TABLE `CustomAction` (`Action` CHAR(72) NOT NULL, `Type` SHORT NOT NULL, `Source` CHAR(72), `Target` CHAR(255) PRIMARY KEY `Action`)",
            "CREATE TABLE `InstallExecuteSequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)",
            "CREATE TABLE `InstallUISequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)"
        };
        foreach (var sql in tables) Execute(sql);
    }

    static void WriteSummary()
    {
        IntPtr summary;
        Check(MsiGetSummaryInformation(db, null, 20, out summary), "MsiGetSummaryInformation");
        try
        {
            var none = new FILETIME();
            const uint VT_I2 = 2, VT_I4 = 3, VT_LPSTR = 30;
            Check(MsiSummaryInfoSetProperty(summary, 1, VT_I2, 1252, ref none, null), "codepage");
            Check(MsiSummaryInfoSetProperty(summary, 2, VT_LPSTR, 0, ref none, "Installation Database"), "title");
            Check(MsiSummaryInfoSetProperty(summary, 3, VT_LPSTR, 0, ref none, ProductName), "subject");
            Check(MsiSummaryInfoSetProperty(summary, 4, VT_LPSTR, 0, ref none, Manufacturer), "author");
            Check(MsiSummaryInfoSetProperty(summary, 7, VT_LPSTR, 0, ref none, "x64;1036"), "template");
            Check(MsiSummaryInfoSetProperty(summary, 9, VT_LPSTR, 0, ref none, NewGuid()), "package code");
            Check(MsiSummaryInfoSetProperty(summary, 14, VT_I4, 200, ref none, null), "schema");
            Check(MsiSummaryInfoSetProperty(summary, 15, VT_I4, 2, ref none, null), "word count");
            Check(MsiSummaryInfoSetProperty(summary, 18, VT_LPSTR, 0, ref none, "Nomi MsiBuilder"), "app name");
            Check(MsiSummaryInfoSetProperty(summary, 19, VT_I4, 2, ref none, null), "security");
            Check(MsiSummaryInfoPersist(summary), "MsiSummaryInfoPersist");
        }
        finally { MsiCloseHandle(summary); }
    }

    static void Insert(string table, string columns, params object[] values)
    {
        var cols = columns.Split(',');
        string sql = "INSERT INTO `" + table + "` (" + string.Join(", ", cols.Select(c => "`" + c + "`")) +
                     ") VALUES (" + string.Join(", ", cols.Select(c => "?")) + ")";
        Execute(sql, values);
    }

    static void Execute(string sql, params object[] values)
    {
        IntPtr view;
        Check(MsiDatabaseOpenView(db, sql, out view), sql);
        IntPtr record = IntPtr.Zero;
        try
        {
            if (values.Length > 0)
            {
                record = MsiCreateRecord((uint)values.Length);
                for (int i = 0; i < values.Length; i++)
                {
                    uint field = (uint)i + 1;
                    object v = values[i];
                    if (v == null) continue;
                    if (v is int) Check(MsiRecordSetInteger(record, field, (int)v), sql);
                    else if (v is FileData) Check(MsiRecordSetStream(record, field, ((FileData)v).Path), sql);
                    else Check(MsiRecordSetString(record, field, v.ToString()), sql);
                }
            }
            Check(MsiViewExecute(view, record), sql);
        }
        finally
        {
            MsiViewClose(view);
            MsiCloseHandle(view);
            if (record != IntPtr.Zero) MsiCloseHandle(record);
        }
    }

    // Files inside a cabinet are named after their File table key.
    static string MakeCabinet(List<Entry> files, string dir, string name, bool compress)
    {
        var ddf = new StringBuilder();
        ddf.AppendLine(".OPTION EXPLICIT");
        ddf.AppendLine(".Set CabinetNameTemplate=" + name);
        ddf.AppendLine(".Set DiskDirectoryTemplate=\"" + dir + "\"");
        ddf.AppendLine(".Set Cabinet=on");
        ddf.AppendLine(".Set Compress=" + (compress ? "on" : "off"));
        if (compress) { ddf.AppendLine(".Set CompressionType=LZX"); ddf.AppendLine(".Set CompressionMemory=21"); }
        ddf.AppendLine(".Set MaxDiskSize=0");
        ddf.AppendLine(".Set CabinetFileCountThreshold=0");
        ddf.AppendLine(".Set FolderFileCountThreshold=0");
        ddf.AppendLine(".Set FolderSizeThreshold=0");
        ddf.AppendLine(".Set MaxCabinetSize=0");
        ddf.AppendLine(".Set UniqueFiles=on");
        ddf.AppendLine(".Set RptFileName=nul");
        ddf.AppendLine(".Set InfFileName=nul");
        foreach (var f in files) ddf.AppendLine("\"" + f.Source + "\" \"" + f.Key + "\"");
        string ddfPath = Path.Combine(dir, Path.ChangeExtension(name, ".ddf"));
        File.WriteAllText(ddfPath, ddf.ToString(), Encoding.Default);

        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "makecab.exe"), "/F \"" + ddfPath + "\"")
        { WorkingDirectory = dir, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        using (var p = Process.Start(psi))
        {
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            string cab = Path.Combine(dir, name);
            if (p.ExitCode != 0 || !File.Exists(cab)) throw new Exception("makecab failed (" + p.ExitCode + "): " + output);
            return cab;
        }
    }

    static string Id(string relative)
    {
        var sb = new StringBuilder();
        foreach (char c in relative) sb.Append(char.IsLetterOrDigit(c) && c < 128 ? c : '_');
        string id = sb.ToString();
        // Identifiers are limited to 72 characters; keep them unique with a hash suffix.
        if (id.Length > 60) id = id.Substring(0, 50) + "_" + Hash(relative).Substring(0, 8);
        return id;
    }

    static string ShortName(string name, int index, bool directory)
    {
        string ext = directory ? "" : Path.GetExtension(name).TrimStart('.');
        if (ext.Length > 3) ext = ext.Substring(0, 3);
        string stem = (directory ? "D" : "F") + (directory ? Hash(name).Substring(0, 6) : index.ToString("D5"));
        return ext.Length > 0 ? stem + "." + ext : stem;
    }

    static string Hash(string s)
    {
        using (var md5 = MD5.Create())
            return BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(s.ToLowerInvariant()))).Replace("-", "");
    }

    // Same file, same component GUID across builds (component rules).
    static string StableGuid(string relative)
    {
        using (var md5 = MD5.Create())
        {
            byte[] b = md5.ComputeHash(Encoding.UTF8.GetBytes(UpgradeCode + "|" + relative.ToLowerInvariant()));
            b[6] = (byte)((b[6] & 0x0F) | 0x30);
            b[8] = (byte)((b[8] & 0x3F) | 0x80);
            return new Guid(b).ToString("B").ToUpperInvariant();
        }
    }

    static string GetFileVersion(string path)
    {
        string v = TryGetFileVersion(path);
        if (v == null) throw new Exception("no version resource in " + path);
        return v;
    }

    static string TryGetFileVersion(string path)
    {
        var version = new StringBuilder(64);
        var lang = new StringBuilder(64);
        uint vLen = 64, lLen = 64;
        uint r = MsiGetFileVersion(path, version, ref vLen, lang, ref lLen);
        return r == 0 && version.Length > 0 ? version.ToString() : null;
    }

    static string NewGuid() { return Guid.NewGuid().ToString("B").ToUpperInvariant(); }

    static void Check(uint result, string what)
    {
        if (result != 0) throw new Exception(what + " -> code " + result);
    }
}
