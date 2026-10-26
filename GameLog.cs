using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GroupRun
{
    public enum GameEventKind
    {
        LevelUp,   // this character levelled up
        Death,     // this character died
        Zone,      // "You have entered ..."
        Area,      // the game generated an area: its level, internal id and seed (just before Zone)
        Load,      // connecting to an instance server: a loading screen starts
        Joined,    // a player joined this instance
        Left,      // a player left this instance
        Afk,       // AFK mode turned on or off
        System,    // any other game system message, kept raw for later analysis
    }

    public sealed class GameEvent
    {
        public GameEventKind Kind;
        public DateTime LoggedAt;   // the line's own timestamp (seconds)
        public long Tick;           // the line's millisecond counter, for exact gaps between lines
        public int Level;           // LevelUp, Area
        public string Text = "";    // Zone name, Area id, player name (also on LevelUp and Death), AFK state, or the raw system message
        public long Seed;           // Area
    }

    /// <summary>
    /// Follows Client.txt from the moment the app starts. Keeps what matters for timing a run: zones and
    /// their areas, loading screens, this character's levels and deaths, who joins and leaves the instance,
    /// AFK, and other system messages raw. Chat in any channel is never kept. Level-ups and deaths carry the
    /// character's name, since party members' show up in the same log; with a known character name, other
    /// players' are dropped here, otherwise the caller decides which name is this runner.
    /// </summary>
    public sealed class GameLog
    {
        // 2026/10/24 20:15:03 123456789 cffb0734 [INFO Client 1234] : You have entered The Coast.
        static readonly Regex Line = new Regex(@"^(\d{4}/\d\d/\d\d \d\d:\d\d:\d\d) (\d+) \S+ \[(\w+) Client \d+\] (.*)$", RegexOptions.Compiled);
        static readonly Regex LevelUp = new Regex(@"^: (.+) \(\w+\) is now level (\d+)$", RegexOptions.Compiled);
        static readonly Regex Slain = new Regex(@"^: (.+) has been slain\.$", RegexOptions.Compiled);
        static readonly Regex Entered = new Regex(@"^: You have entered (.+)\.$", RegexOptions.Compiled);
        static readonly Regex Generating = new Regex(@"^Generating level (\d+) area ""([^""]+)""(?: with seed (\d+))?", RegexOptions.Compiled);
        static readonly Regex Connecting = new Regex(@"^Connecting to instance server", RegexOptions.Compiled);
        static readonly Regex JoinedArea = new Regex(@"^: (.+) has joined the area\.$", RegexOptions.Compiled);
        static readonly Regex LeftArea = new Regex(@"^: (.+) has left the area\.$", RegexOptions.Compiled);
        static readonly Regex AfkMode = new Regex(@"^: AFK mode is now (ON|OFF)", RegexOptions.Compiled);

        readonly string path;
        readonly string character;
        long position;
        string partial = "";
        readonly Decoder utf8 = Encoding.UTF8.GetDecoder(); // keeps a character cut in two by a read for the next one

        public GameLog(string path, string character)
        {
            this.path = path;
            this.character = (character ?? "").Trim();
            position = File.Exists(path) ? new FileInfo(path).Length : 0;
        }

        public bool Exists => File.Exists(path);

        /// <summary>
        /// What the log said in its last <paramref name="bytes"/> before the app started, for an app started in
        /// the middle of a session (a restart, a crash): never read as new.
        /// </summary>
        public List<GameEvent> ReadEarlier(int bytes)
        {
            var events = new List<GameEvent>();
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var from = Math.Max(0, Math.Min(position, stream.Length) - bytes);
                    var buffer = new byte[Math.Min(position, stream.Length) - from];
                    stream.Seek(from, SeekOrigin.Begin);
                    var read = stream.Read(buffer, 0, buffer.Length);
                    var lines = Encoding.UTF8.GetString(buffer, 0, read).Split('\n');
                    for (var i = from > 0 ? 1 : 0; i < lines.Length; i++) // a cut first line isn't a line
                        if (Parse(lines[i].TrimEnd('\r')) is GameEvent e)
                            events.Add(e);
                }
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                // no log yet, or not readable now: start without it
            }
            return events;
        }

        public List<GameEvent> ReadNew()
        {
            var events = new List<GameEvent>();
            if (!Exists)
                return events;

            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return events; // another program holds it without sharing, or it was deleted just now: next tick
            }
            using (stream)
            {
                if (stream.Length < position)
                {
                    position = 0; // log was truncated or replaced: so is what was left of its last line
                    partial = "";
                    utf8.Reset();
                }
                if (stream.Length == position)
                    return events;

                stream.Seek(position, SeekOrigin.Begin);
                var buffer = new byte[stream.Length - position];
                var read = stream.Read(buffer, 0, buffer.Length);
                position += read;

                var text = new char[utf8.GetCharCount(buffer, 0, read)];
                utf8.GetChars(buffer, 0, read, text, 0);
                var lines = (partial + new string(text)).Split('\n');
                partial = lines[lines.Length - 1];
                for (var i = 0; i < lines.Length - 1; i++)
                {
                    var parsed = Parse(lines[i].TrimEnd('\r'));
                    if (parsed != null)
                        events.Add(parsed);
                }
            }
            return events;
        }

        /// <summary>Character names are unique whatever their case, so the one typed in Settings needn't match it.</summary>
        bool IsCharacter(string name) => string.Equals(name, character, StringComparison.OrdinalIgnoreCase);

        GameEvent Parse(string raw)
        {
            var match = Line.Match(raw);
            if (!match.Success)
                return null;
            var e = new GameEvent
            {
                LoggedAt = DateTime.ParseExact(match.Groups[1].Value, "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture),
                Tick = long.Parse(match.Groups[2].Value),
            };
            var message = match.Groups[4].Value;

            if (Generating.Match(message) is Match area && area.Success)
            {
                e.Kind = GameEventKind.Area;
                e.Level = int.Parse(area.Groups[1].Value);
                e.Text = area.Groups[2].Value;
                e.Seed = area.Groups[3].Success ? long.Parse(area.Groups[3].Value) : 0;
                return e;
            }
            if (Connecting.IsMatch(message))
            {
                e.Kind = GameEventKind.Load; // the server address on this line is not kept
                return e;
            }
            if (!message.StartsWith(": "))
                return null; // chat ("#", "$", "@", "%", "&" or a name) and engine noise

            if (LevelUp.Match(message) is Match level && level.Success)
            {
                if (character.Length > 0 && !IsCharacter(level.Groups[1].Value)) return null;
                e.Kind = GameEventKind.LevelUp;
                e.Level = int.Parse(level.Groups[2].Value);
                e.Text = level.Groups[1].Value;
            }
            else if (Slain.Match(message) is Match slain && slain.Success)
            {
                if (character.Length > 0 && !IsCharacter(slain.Groups[1].Value)) return null;
                e.Kind = GameEventKind.Death;
                e.Text = slain.Groups[1].Value;
            }
            else if (Entered.Match(message) is Match entered && entered.Success)
            {
                e.Kind = GameEventKind.Zone;
                e.Text = entered.Groups[1].Value;
            }
            else if (JoinedArea.Match(message) is Match joined && joined.Success)
            {
                e.Kind = GameEventKind.Joined;
                e.Text = joined.Groups[1].Value;
            }
            else if (LeftArea.Match(message) is Match left && left.Success)
            {
                e.Kind = GameEventKind.Left;
                e.Text = left.Groups[1].Value;
            }
            else if (AfkMode.Match(message) is Match afk && afk.Success)
            {
                e.Kind = GameEventKind.Afk;
                e.Text = afk.Groups[1].Value.ToLowerInvariant();
            }
            else
            {
                e.Kind = GameEventKind.System;
                e.Text = message.Substring(2);
            }
            return e;
        }
    }
}
