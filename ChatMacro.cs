using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GroupRun
{
    /// <summary>
    /// The callout key: while the game is the active window, a press types one party-chat line in game (one chat
    /// message per key press, like Lutbot's) and the window tells the group. Everywhere else the key does what
    /// it normally does. Watches the keyboard with a low-level hook, so it needs the app's message loop.
    /// </summary>
    public sealed class ChatMacro : IDisposable
    {
        readonly Keys key;
        readonly Keys modifiers;
        readonly HookProc proc; // kept alive while hooked
        readonly IntPtr hook;

        /// <summary>Raised on the UI thread when the key is pressed in game.</summary>
        public event Action Pressed;

        /// <param name="binding">"Ctrl+Z", "F8", "Alt+Shift+Q"…; empty means no key.</param>
        public ChatMacro(string binding)
        {
            if (!TryParse(binding, out key, out modifiers))
                return;
            proc = OnKey;
            using (var self = Process.GetCurrentProcess())
            using (var module = self.MainModule)
                hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(module.ModuleName), 0);
        }

        public static bool TryParse(string binding, out Keys key, out Keys modifiers)
        {
            key = Keys.None;
            modifiers = Keys.None;
            var parts = (binding ?? "").Split('+').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (parts.Count == 0)
                return false;
            foreach (var part in parts.Take(parts.Count - 1))
                switch (part.ToLowerInvariant())
                {
                    case "ctrl": case "control": modifiers |= Keys.Control; break;
                    case "alt": modifiers |= Keys.Alt; break;
                    case "shift": modifiers |= Keys.Shift; break;
                    default: return false;
                }
            return Enum.TryParse(parts.Last(), true, out key) && key != Keys.None;
        }

        IntPtr OnKey(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && (message == (IntPtr)WM_KEYDOWN || message == (IntPtr)WM_SYSKEYDOWN))
            {
                var pressed = (Keys)Marshal.ReadInt32(data);
                if (pressed == key && Control.ModifierKeys == modifiers && XpBar.GameIsForeground())
                {
                    // after this callback returns: typing from inside the hook would feed our own keys back in
                    Application.OpenForms.Cast<Form>().FirstOrDefault()?.BeginInvoke(new Action(() => Pressed?.Invoke()));
                    return (IntPtr)1; // the game doesn't see the key itself
                }
            }
            return CallNextHookEx(hook, code, message, data);
        }

        /// <summary>Type one line into the game's chat: Enter, the text, Enter.</summary>
        public static void Type(string line)
        {
            var inputs = new System.Collections.Generic.List<INPUT>();
            ReleaseModifiers(inputs); // the callout key's Ctrl is still held: Ctrl+Enter would whisper instead
            Key(inputs, VK_RETURN);
            foreach (var c in line)
            {
                inputs.Add(Unicode(c, 0));
                inputs.Add(Unicode(c, KEYEVENTF_KEYUP));
            }
            Key(inputs, VK_RETURN);
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        }

        static void ReleaseModifiers(System.Collections.Generic.List<INPUT> inputs)
        {
            foreach (var vk in new ushort[] { 0x11, 0x12, 0x10 }) // Ctrl, Alt, Shift
                if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                    inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } });
        }

        static void Key(System.Collections.Generic.List<INPUT> inputs, ushort vk)
        {
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk } });
            inputs.Add(new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } });
        }

        static INPUT Unicode(char c, uint flags) =>
            new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | flags } };

        public void Dispose()
        {
            if (hook != IntPtr.Zero)
                UnhookWindowsHookEx(hook);
        }

        const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
        const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;
        const ushort VK_RETURN = 0x0D;

        delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        // Windows' INPUT is a union sized by its largest member (MOUSEINPUT); keyboard input is all this sends
        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT
        {
            public uint type;
            public InputUnion u;
            public KEYBDINPUT ki { set => u.ki = value; }
        }

        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    }
}
