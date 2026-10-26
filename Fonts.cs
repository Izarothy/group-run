using System;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;

namespace GroupRun
{
    /// <summary>
    /// Geist and Geist Mono (SIL Open Font License, fonts/OFL.txt), embedded in the exe so the panel looks the
    /// same on every PC. GDI+ exposes each weight as its own family: "Geist", "Geist Medium", "Geist SemiBold",
    /// "Geist Mono", "Geist Mono Medium".
    /// </summary>
    public static class Fonts
    {
        static readonly PrivateFontCollection Collection = Load();

        public static Font Sans(float px) => Get("Geist", px);
        public static Font SansMedium(float px) => Get("Geist Medium", px);
        public static Font SansSemiBold(float px) => Get("Geist SemiBold", px);
        public static Font Mono(float px) => Get("Geist Mono", px);
        public static Font MonoMedium(float px) => Get("Geist Mono Medium", px);

        static Font Get(string family, float px)
        {
            foreach (var f in Collection.Families)
                if (f.Name == family)
                    return new Font(f, px, FontStyle.Regular, GraphicsUnit.Pixel);
            return new Font("Segoe UI", px, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        [DllImport("gdi32.dll")]
        static extern IntPtr AddFontMemResourceEx(IntPtr font, uint length, IntPtr reserved, ref uint fontsAdded);

        static PrivateFontCollection Load()
        {
            var collection = new PrivateFontCollection();
            var assembly = typeof(Fonts).Assembly;
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (!name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase))
                    continue;
                using (var stream = assembly.GetManifestResourceStream(name))
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    var bytes = memory.ToArray();
                    // the collection reads the font from this memory for as long as it lives, so it is never freed
                    var pointer = Marshal.AllocCoTaskMem(bytes.Length);
                    Marshal.Copy(bytes, 0, pointer, bytes.Length);
                    collection.AddMemoryFont(pointer, bytes.Length);
                    uint added = 0; // also for GDI, which draws the standard controls' text and can't see the collection
                    AddFontMemResourceEx(pointer, (uint)bytes.Length, IntPtr.Zero, ref added);
                }
            }
            return collection;
        }
    }
}
