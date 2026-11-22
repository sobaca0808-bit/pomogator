using System.Drawing;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

namespace Pomogator.Vpn;

internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly Forms.ToolStripMenuItem disconnect;
    private Icon? artwork;
    internal Phase? Current { get; private set; }

    internal TrayIcon(Action open, Action stop, Action quit)
    {
        menu.Items.Add("Открыть", null, (_, _) => open());
        disconnect = (Forms.ToolStripMenuItem)menu.Items.Add("Отключиться", null, (_, _) => stop());
        menu.Items.Add("Выход", null, (_, _) => quit());
        icon = new Forms.NotifyIcon { ContextMenuStrip = menu };
        icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) open(); };
        Set(Phase.Disconnected);
        icon.Visible = true;
    }

    internal void Set(Phase phase)
    {
        if (Current == phase) return;
        var state = StatusPresentation.For(phase);
        var next = Draw(state.Color, state.Glyph);
        icon.Icon = next;
        icon.Text = "ПОМОГАТОР VPN — " + state.Label;
        disconnect.Enabled = phase is Phase.Connected or Phase.Unstable;
        artwork?.Dispose();
        artwork = next;
        Current = phase;
    }

    private static Icon Draw(string color, string glyph)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var fill = new SolidBrush(ColorTranslator.FromHtml(color));
        using var ink = new SolidBrush(Color.FromArgb(15, 23, 34));
        using var position = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var font = new Font("Segoe UI Symbol", 18, FontStyle.Bold, GraphicsUnit.Pixel);
        graphics.FillEllipse(fill, 1, 1, 30, 30);
        graphics.DrawString(glyph, font, ink, new RectangleF(1, 0, 30, 30), position);
        var handle = bitmap.GetHicon();
        try { using var source = Icon.FromHandle(handle); return (Icon)source.Clone(); }
        finally { DestroyIcon(handle); }
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        menu.Dispose();
        artwork?.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}

internal sealed record StatusPresentation(string Label, string Glyph, string Color)
{
    internal static StatusPresentation For(Phase phase) => phase switch
    {
        Phase.Connecting => new("Подключается", "…", "#F9CB72"),
        Phase.Connected => new("Подключён и проверен", "✓", "#78E5AC"),
        Phase.Unstable => new("Связь нестабильна", "!", "#F9CB72"),
        Phase.Error => new("Ошибка подключения", "!", "#F58E9D"),
        Phase.Disconnecting => new("Отключается", "↘", "#76C9FF"),
        _ => new("Отключён", "–", "#A6B4C8")
    };
}
