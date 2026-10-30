using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Speech.Synthesis;

namespace GroupRun
{
    /// <summary>
    /// Says the countdown and what happens to the run out loud, with Windows' built-in text to speech, so
    /// nobody has to look at the board mid-game. Prefers an English voice when one is installed.
    /// </summary>
    public sealed class Voice : IDisposable
    {
        readonly SpeechSynthesizer synth = new SpeechSynthesizer();

        public bool Enabled = true;

        // names the synthesizer spells out letter by letter, written the way they're said
        static readonly Dictionary<string, string> Pronounced = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["iza"] = "eeza",
        };

        public Voice()
        {
            var english = synth.GetInstalledVoices()
                .FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "en");
            if (english != null)
                synth.SelectVoice(english.VoiceInfo.Name);
            synth.Rate = 1;
        }

        /// <summary>Say <paramref name="text"/>, cutting off anything still being said so the count stays on time.</summary>
        public void Say(string text)
        {
            if (!Enabled)
                return;
            synth.SpeakAsyncCancelAll();
            synth.SpeakAsync(Regex.Replace(text, @"\w+", w => Pronounced.TryGetValue(w.Value, out var said) ? said : w.Value));
        }

        public void Dispose() => synth.Dispose();
    }
}
