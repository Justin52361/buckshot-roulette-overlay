using System;
using System.IO;
using System.Media;
using System.Threading.Tasks;

namespace BuckshotOverlay
{
    public static class SoundPlayerHelper
    {
        public static bool SoundEnabled { get; set; } = true;

        public static void PlayLiveFired()
        {
            if (!SoundEnabled) return;
            Task.Run(() =>
            {
                try
                {
                    // Sharp lower pitch alert (Live round racked/fired)
                    Console.Beep(440, 90);
                }
                catch
                {
                    SystemSounds.Asterisk.Play();
                }
            });
        }

        public static void PlayBlankFired()
        {
            if (!SoundEnabled) return;
            Task.Run(() =>
            {
                try
                {
                    // High crisp click (Blank round racked/fired)
                    Console.Beep(880, 70);
                }
                catch
                {
                    SystemSounds.Hand.Play();
                }
            });
        }

        public static void PlayReload()
        {
            if (!SoundEnabled) return;
            Task.Run(() =>
            {
                try
                {
                    // Two-tone reload chime
                    Console.Beep(520, 70);
                    Console.Beep(680, 110);
                }
                catch
                {
                    SystemSounds.Question.Play();
                }
            });
        }

        public static void PlayToggle()
        {
            if (!SoundEnabled) return;
            Task.Run(() =>
            {
                try
                {
                    Console.Beep(750, 60);
                }
                catch
                {
                    SystemSounds.Beep.Play();
                }
            });
        }
    }
}
