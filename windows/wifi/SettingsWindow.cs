using System;
using System.Drawing;
using System.Windows.Forms;

namespace PomogatorWifi
{
    internal sealed class SettingsWindow : GlassForm
    {
        private readonly TextBox name = new TextBox();
        private readonly TextBox key = new TextBox { UseSystemPasswordChar = true, MaxLength = 63 };
        private readonly CheckBox launch, autoWifi, close, restore;
        private readonly Label status = Theme.Label("", 9, Theme.ColorOf(WifiPhase.Error));
        private readonly UserSettings original;
        internal UserSettings Result { get; private set; }

        internal SettingsWindow(UserSettings current, WifiPhase phase) : base("Настройки · ПОМОГАТОР Wi-Fi", new Size(492, 626))
        {
            original = current;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            var root = WifiWindow.Rows(46, 184, 253, 51, 57);
            root.Padding = new Padding(21, 9, 21, 10);
            root.Controls.Add(Theme.Label("Настройки", 19, bold: true), 0, 0);

            var network = new GlassPanel { Dock = DockStyle.Fill, Padding = new Padding(16, 7, 16, 7), Margin = new Padding(0, 0, 0, 9) };
            var fields = WifiWindow.Rows(29, 35, 25, 36, 0);
            fields.Controls.Add(Theme.Label("Имя сети Wi-Fi", 10, bold: true), 0, 0);
            fields.Controls.Add(Theme.Label("Пароль Wi-Fi", 10, bold: true), 0, 2);
            name.Text = current.Ssid;
            key.Text = current.Password;
            Prepare(name, "Имя сети Wi-Fi");
            Prepare(key, "Пароль Wi-Fi");
            fields.Controls.Add(name, 0, 1);
            var passwordFields = WifiWindow.Columns(0, 104);
            passwordFields.Controls.Add(key, 0, 0);
            var show = Theme.Button("Показать", (_, __) => { });
            show.Margin = new Padding(5, 2, 0, 3);
            show.Click += (_, __) => { key.UseSystemPasswordChar = !key.UseSystemPasswordChar; show.Text = key.UseSystemPasswordChar ? "Показать" : "Скрыть"; show.AccessibleName = show.Text + " пароль"; };
            show.AccessibleName = "Показать пароль";
            passwordFields.Controls.Add(show, 1, 0);
            fields.Controls.Add(passwordFields, 0, 3);
            fields.Controls.Add(Theme.Label(phase == WifiPhase.External || phase == WifiPhase.Owned ?
                "Параметры вступят в силу при следующем запуске точки. Остановка точки отключит устройства." :
                "Имя до 32 байт · пароль от 8 до 63 символов ASCII.", 9, Theme.Muted), 0, 4);
            network.Controls.Add(fields);
            root.Controls.Add(network, 0, 1);

            var automatic = new GlassPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(16, 7, 16, 6) };
            var options = WifiWindow.Rows(32, 52, 52, 52, 52);
            options.Controls.Add(Theme.Label("Автоматизация", 13, bold: true), 0, 0);
            launch = AddOption(options, 1, "Запускать вместе с Windows", "Откроет приложение при входе в Windows.", current.AutoLaunch);
            autoWifi = AddOption(options, 2, "Автоматически включать Wi-Fi", "При открытии приложения запустит свободную точку.", current.AutoWifi);
            close = AddOption(options, 3, "Сворачивать в системный трей", "Крестик скрывает окно. Раздача продолжается.", current.CloseToTray);
            restore = AddOption(options, 4, "Восстанавливать раздачу", "После возврата PPPoE. Только для своего сеанса и прежней пары ICS.", current.AutoRestore);
            automatic.Controls.Add(options);
            root.Controls.Add(automatic, 0, 2);
            root.Controls.Add(status, 0, 3);
            var buttons = WifiWindow.Columns(0, 157, 114);
            var save = Theme.Button("Сохранить изменения", (_, __) => Save());
            save.Margin = new Padding(0, 4, 8, 8);
            buttons.Controls.Add(save, 1, 0);
            var cancel = Theme.Button("Отмена", (_, __) => DialogResult = DialogResult.Cancel);
            cancel.Margin = new Padding(0, 4, 0, 8);
            buttons.Controls.Add(cancel, 2, 0);
            root.Controls.Add(buttons, 0, 4);
            Controls.Add(root);
            AcceptButton = save;
            CancelButton = cancel;
        }

        private static void Prepare(TextBox box, string accessibleName)
        {
            box.AccessibleName = accessibleName;
            box.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            box.Margin = new Padding(1, 2, 1, 2);
            box.BackColor = Theme.Input;
            box.ForeColor = Theme.Text;
            box.BorderStyle = BorderStyle.FixedSingle;
        }

        private static CheckBox AddOption(TableLayoutPanel parent, int row, string heading, string hint, bool enabled)
        {
            var layout = WifiWindow.Rows(26, 26);
            var box = new CheckBox { Text = heading, AccessibleDescription = hint, Checked = enabled, Dock = DockStyle.Fill,
                BackColor = Color.Transparent, ForeColor = Theme.Text, Margin = Padding.Empty };
            var note = Theme.Label(hint, 8.5f, Theme.Muted);
            note.Padding = new Padding(18, 0, 0, 0);
            layout.Controls.Add(box, 0, 0);
            layout.Controls.Add(note, 0, 1);
            parent.Controls.Add(layout, 0, row);
            return box;
        }

        private void Save()
        {
            var data = new UserSettings { Ssid = name.Text, Password = key.Text, AutoLaunch = launch.Checked,
                AutoWifi = autoWifi.Checked, CloseToTray = close.Checked, AutoRestore = restore.Checked };
            try
            {
                Policy.EncodeSsid(data.Ssid);
                if (data.Password.Length != 0) Policy.CheckPassword(data.Password);
                if (data.AutoLaunch != original.AutoLaunch) Local.SetAutostart(data.AutoLaunch, Wire.Executable);
                try { Local.Save(Local.SettingsPath, data); }
                catch
                {
                    if (data.AutoLaunch != original.AutoLaunch) Local.SetAutostart(original.AutoLaunch, Wire.Executable);
                    throw;
                }
                Local.Log("settings_saved");
                Result = data;
                DialogResult = DialogResult.OK;
            }
            catch (Exception error)
            {
                Local.LogError("settings_save_failed", error);
                status.Text = error is WifiError ? error.Message : "Windows не сохранила параметры. Проверьте права своей учётной записи.";
            }
        }
    }
}
