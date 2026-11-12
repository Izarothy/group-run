using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace GroupRun
{
    /// <summary>Whether Path of Exile is running and in front. Nothing is read from its window.</summary>
    public static class Game
    {
        static readonly string[] ProcessNames =
        {
            "PathOfExile", "PathOfExile_x64", "PathOfExileSteam", "PathOfExile_x64Steam",
            "PathOfExileEGS", "PathOfExile_x64EGS", "PathOfExile_KG", "PathOfExile_x64_KG",
        };

        static IntPtr[] Windows() =>
            ProcessNames.SelectMany(Process.GetProcessesByName).Select(p => p.MainWindowHandle).Where(h => h != IntPtr.Zero).ToArray();

        public static bool IsRunning() => Windows().Length > 0;

        /// <summary>Whether the game is the window in front, i.e. the runner hasn't tabbed out.</summary>
        public static bool IsForeground()
        {
            var front = GetForegroundWindow();
            return front != IntPtr.Zero && Windows().Contains(front);
        }

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
    }
}
