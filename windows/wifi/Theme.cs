using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;

namespace PomogatorWifi
{
    internal static class Theme
    {
        internal static readonly Color Text = Color.FromArgb(239, 247, 255);
        internal static readonly Color Muted = Color.FromArgb(161, 182, 209);
        internal static readonly Color Glass = Color.FromArgb(23, 40, 62);
        internal static readonly Color Input = Color.FromArgb(15, 28, 46);
        internal static readonly Color Accent = Color.FromArgb(109, 238, 211);

        internal static Icon Brand { get; } = ReadIcon();
        internal static Image Mark { get; } = ReadMark();

        private static Image ReadMark()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WifiMark"))
            using (var image = Image.FromStream(stream)) return new Bitmap(image);
        }

        private static Icon ReadIcon()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WifiIcon"))
            using (var icon = new Icon(stream)) return (Icon)icon.Clone();
        }

        internal static Color ColorOf(WifiPhase phase)
        {
            switch (phase)
            {
                case WifiPhase.Owned:
                case WifiPhase.External: return Accent;
                case WifiPhase.Starting: return Color.FromArgb(255, 214, 125);
                case WifiPhase.Stopping: return Color.FromArgb(124, 197, 255);
                case WifiPhase.Error: return Color.FromArgb(255, 146, 160);
                default: return Color.FromArgb(172, 190, 213);
            }
        }

        internal static GraphicsPath Round(Rectangle bounds, int radius)
        {
            var path = new GraphicsPath();
            var size = radius * 2;
            path.AddArc(bounds.Left, bounds.Top, size, size, 180, 90);
            path.AddArc(bounds.Right - size, bounds.Top, size, size, 270, 90);
            path.AddArc(bounds.Right - size, bounds.Bottom - size, size, size, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - size, size, size, 90, 90);
            path.CloseFigure();
            return path;
        }

        internal static Label Label(string text, float size = 10, Color? color = null, bool bold = false)
        {
            return new Label { Text = text, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
                ForeColor = color ?? Text, BackColor = Color.Transparent, Dock = DockStyle.Fill,
                Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
        }

        internal static Button Button(string text, EventHandler action)
        {
            var button = new GlassButton { Text = text, AccessibleName = text, Dock = DockStyle.Fill };
            button.Click += action;
            return button;
        }

        internal static void Background(Graphics graphics, Rectangle bounds)
        {
            if (bounds.Width < 1 || bounds.Height < 1) return;
            using (var brush = new LinearGradientBrush(bounds, Color.FromArgb(13, 24, 43), Color.FromArgb(7, 13, 27), 105f))
                graphics.FillRectangle(brush, bounds);
            using (var shape = new GraphicsPath())
            {
                shape.AddEllipse(-bounds.Width / 3, -bounds.Height / 2, bounds.Width * 9 / 5, bounds.Height * 5 / 4);
                using (var light = new PathGradientBrush(shape) { CenterColor = Color.FromArgb(100, 37, 87, 113),
                    SurroundColors = new[] { Color.FromArgb(0, 37, 87, 113) } })
                    graphics.FillPath(light, shape);
            }
        }
    }

    internal class GlassForm : Form
    {
        protected GlassForm(string title, Size size)
        {
            Text = title;
            Icon = Theme.Brand;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            Font = new Font("Segoe UI", 10f);
            ForeColor = Theme.Text;
            BackColor = Color.FromArgb(12, 23, 40);
            ClientSize = size;
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            var enabled = 1;
            if (DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int)) < 0)
                DwmSetWindowAttribute(Handle, 19, ref enabled, sizeof(int));
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int bytes);

        protected override void OnPaintBackground(PaintEventArgs e) => Theme.Background(e.Graphics, ClientRectangle);
    }

    internal sealed class GlassPanel : Panel
    {
        internal GlassPanel() { DoubleBuffered = true; BackColor = Color.Transparent; }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            if (Width < 36 || Height < 36) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new Rectangle(1, 1, Width - 3, Height - 3);
            using (var path = Theme.Round(bounds, Math.Max(12, DeviceDpi * 18 / 96)))
            using (var fill = new LinearGradientBrush(bounds, Color.FromArgb(170, 43, 68, 96),
                Color.FromArgb(190, 17, 29, 48), 55f))
            using (var stroke = new Pen(Color.FromArgb(102, 114, 154, 191)))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(stroke, path);
            }
        }
    }

    internal class GlassButton : Button
    {
        internal GlassButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            ForeColor = Theme.Text;
            BackColor = Color.Transparent;
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            if (Width < 14 || Height < 14) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var hover = ClientRectangle.Contains(PointToClient(Cursor.Position));
            using (var path = Theme.Round(new Rectangle(1, 1, Width - 3, Height - 3), 9))
            using (var fill = new SolidBrush(hover && Enabled ? Color.FromArgb(57, 94, 121) : Color.FromArgb(30, 50, 74)))
            using (var edge = new Pen(Focused ? Theme.Accent : Color.FromArgb(84, 116, 148)))
            {
                e.Graphics.FillPath(fill, path);
                e.Graphics.DrawPath(edge, path);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    internal sealed class PowerButton : GlassButton
    {
        private readonly Timer animation = new Timer { Interval = 50 };
        private double tick;
        private WifiPhase phase;
        internal WifiPhase Phase
        {
            get => phase;
            set { phase = value; animation.Enabled = value == WifiPhase.Starting && SystemInformation.IsMenuAnimationEnabled; Invalidate(); }
        }

        internal PowerButton()
        {
            AccessibleName = "Управление точкой доступа";
            animation.Tick += (_, __) => { tick += 0.15; Invalidate(); };
        }

        protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var size = Math.Min(Width, Height);
            if (size < 20) return;
            var box = new Rectangle((Width - size) / 2, (Height - size) / 2, size, size);
            var color = Theme.ColorOf(Phase);
            for (int i = 0; i < 7; i++)
            {
                var ring = box;
                ring.Inflate(-i * size / 80, -i * size / 80);
                using (var glow = new SolidBrush(Color.FromArgb(6 + i * 2, color))) g.FillEllipse(glow, ring);
            }
            box.Inflate(-size / 11, -size / 11);
            var hovered = Enabled && ClientRectangle.Contains(PointToClient(Cursor.Position));
            using (var face = new LinearGradientBrush(box, Color.FromArgb(65, color), Color.FromArgb(212, 15, 29, 46), 90))
            using (var edge = new Pen(Color.FromArgb(Focused || hovered ? 255 : 180 + (int)(18 * Math.Sin(tick)), color), Focused ? 3 : 2))
            {
                g.FillEllipse(face, box);
                g.DrawEllipse(edge, box);
            }

            var middle = new PointF(box.Left + box.Width / 2f, box.Top + box.Height / 2f);
            var icon = box.Width * 0.28f;
            using (var stroke = new Pen(color, Math.Max(2.5f, size / 32f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawArc(stroke, middle.X - icon / 2, middle.Y - icon / 2 + 3, icon, icon, -53, 286);
                g.DrawLine(stroke, middle.X, middle.Y - icon / 2 - 4, middle.X, middle.Y + 1);
            }
        }
    }
}
