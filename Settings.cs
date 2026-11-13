using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace GroupRun
{
    /// <summary>Everything the app remembers, in settings.json next to the exe.</summary>
    public sealed class Settings
    {
        public string DisplayName = "";
        public string CharacterName = "";   // optional: found automatically from the first level-up
        public string ClientLogPath = "";
        public string GroupCode = "";
        public bool IsAdmin;
        public string RunnerId = Guid.NewGuid().ToString("N").Substring(0, 12);
        public string RunnerMachine = Environment.MachineName; // the PC RunnerId was made on
        public bool Voice = true;
        public bool Gamble;                 // say "gamble axe" and the like in the first town after levels 3, 7, 11, 24 and 28
        public bool Overlay;                // buttons hidden: just the board, for OBS
        public int HostPort = 47321;        // admin: the port runners connect to
        public string HostSecret = "";      // admin: the secret part of the join code, made on first host
        public string HostTopic = "";       // admin: where the current tunnel address is posted, made on first host
        public string MacroKey = "";        // in game, e.g. "Ctrl+Z": type the callout in party chat and tell the group
        public string SplitsFile = "";      // admin: a LiveSplit .lss whose segment names are the group's zone splits
        public string OnTop = "running"; // "running" (while a run is live, as LiveSplit), "always" or "never"
        public string BackgroundColor = "#0a0b0e"; // flat, so OBS can colour-key it

        static string FilePath => Path.Combine(AppContext.BaseDirectory, "settings.json");

        public bool IsComplete => DisplayName.Trim().Length > 0; // a missing join code or character shows on the board instead

        public const string CantSave = "Can't save settings.json next to GroupRun.exe: move the app to a folder you can write to.";

        public static Settings Load()
        {
            Settings loaded = null;
            try
            {
                if (File.Exists(FilePath))
                    loaded = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(FilePath));
            }
            catch (Exception e) when (!(e is IOException || e is UnauthorizedAccessException))
            {
                // cut short by a crash mid-save, or a hand edit gone wrong: set up again rather than never start
            }
            if (loaded == null)
                return new Settings { ClientLogPath = GuessClientLog() };
            if (loaded.RunnerMachine != Environment.MachineName)
            {
                // copied from another PC (a ready-made folder from a friend): two runners with one id share a row on
                // every board and each ignores the other's pause, so this PC gets its own
                loaded.RunnerId = new Settings().RunnerId;
                loaded.RunnerMachine = Environment.MachineName;
                loaded.Save();
            }
            return loaded;
        }

        /// <summary>False when settings.json can't be written: a read-only folder such as Program Files, or the file held open.</summary>
        public bool Save()
        {
            try
            {
                File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this));
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>The usual Client.txt locations for the standalone, Steam and Epic clients.</summary>
        public static string GuessClientLog()
        {
            var candidates = new List<string>
            {
                @"C:\Program Files (x86)\Grinding Gear Games\Path of Exile\logs\Client.txt",
                @"C:\Program Files (x86)\Steam\steamapps\common\Path of Exile\logs\Client.txt",
                @"C:\Program Files\Epic Games\PathOfExile\logs\Client.txt",
            };
            foreach (var drive in new[] { "D", "E", "F" })
            {
                candidates.Add($@"{drive}:\Steam\steamapps\common\Path of Exile\logs\Client.txt");
                candidates.Add($@"{drive}:\SteamLibrary\steamapps\common\Path of Exile\logs\Client.txt");
                candidates.Add($@"{drive}:\Games\Path of Exile\logs\Client.txt");
            }
            return candidates.Find(File.Exists) ?? "";
        }
    }
}
