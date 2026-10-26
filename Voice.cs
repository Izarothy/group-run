using System;
using System.Linq;
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
            synth.SpeakAsync(text);
        }

        public void Dispose() => synth.Dispose();
    }
}
