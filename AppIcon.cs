using System.Drawing;
using System.Drawing.Drawing2D;

namespace GroupRun
{
    /// <summary>The window and tray icon, drawn in code: three standings bars on the board's black, the leader green.</summary>
    public static class AppIcon
    {
        public static Icon Create()
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = new GraphicsPath())
                using (var background = new SolidBrush(PanelView.Background))
                {
                    path.AddArc(0, 0, 12, 12, 180, 90);
                    path.AddArc(19, 0, 12, 12, 270, 90);
                    path.AddArc(19, 19, 12, 12, 0, 90);
                    path.AddArc(0, 19, 12, 12, 90, 90);
                    path.CloseFigure();
                    g.FillPath(background, path);
                }
                using (var green = new SolidBrush(Color.FromArgb(69, 196, 107)))
                using (var text = new SolidBrush(Color.FromArgb(237, 237, 237)))
                {
                    g.FillRectangle(green, 7, 8, 18, 4);
                    g.FillRectangle(text, 7, 14, 14, 4);
                    g.FillRectangle(text, 7, 20, 10, 4);
                }
                return Icon.FromHandle(bitmap.GetHicon());
            }
        }
    }
}
