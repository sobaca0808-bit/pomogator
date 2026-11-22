using System;
using System.Linq;
using System.Text;
using System.Threading;

namespace PomogatorWifiLite
{
    internal enum PointState { Unavailable, Idle, Active }

    internal sealed class PointStatus
    {
        internal PointState State;
        internal Guid Adapter;
        internal uint Clients;
        internal bool Ours, OtherController;
        internal string Detail;
    }

    internal interface IHostedNetwork : IDisposable
    {
        PointStatus Read();
        void StartUsing(byte[] ssid, string password);
        void StopUsing();
        bool Interrupted { get; }
    }

    internal sealed class HostedSession : IDisposable
    {
        private readonly Func<IHostedNetwork> open;
        private readonly Func<bool> previousRunning;
        private readonly Action delay;
        private IHostedNetwork lease;
        private Guid adapter;
        private string lastLoss;

        internal HostedSession(Func<IHostedNetwork> open, Func<bool> previousRunning, Action delay = null)
        {
            this.open = open;
            this.previousRunning = previousRunning;
            this.delay = delay ?? (() => Thread.Sleep(150));
        }

        internal bool CanStop => lease != null;

        internal PointStatus Read()
        {
            var previous = previousRunning();
            if (lease != null && previous)
                Dispose();
            PointStatus status;
            if (lease != null)
            {
                status = lease.Read();
                if (status.State == PointState.Active && status.Adapter == adapter && !lease.Interrupted)
                    status.Ours = true;
                else
                {
                    var interrupted = status.Detail;
                    Dispose();
                    // Closing the handle can change the count of clients holding the hosted network.
                    using (var fresh = open()) status = fresh.Read();
                    lastLoss = "Windows завершила исходный сеанс или изменила виртуальный адаптер. " + interrupted;
                }
            }
            else using (var observer = open()) status = observer.Read();

            if (previous)
                status.Detail = "Обнаружен прежний ПОМОГАТОР Wi-Fi. Завершите его сеанс перед новым запуском. " + status.Detail;
            if (lastLoss != null) status.Detail = lastLoss + " " + status.Detail;
            status.OtherController = previous;
            return status;
        }

        internal void Start(string name, string password)
        {
            if (previousRunning()) throw new WifiException("Завершите прежний ПОМОГАТОР Wi-Fi перед запуском.");
            if (lease != null) throw new WifiException("Сеанс уже открыт. Сначала нажмите «Остановить».");
            lastLoss = null;
            var ssid = Encoding.UTF8.GetBytes(name);
            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) || ssid.Length > 32)
                throw new WifiException("SSID должен содержать от 1 до 32 байт UTF-8 без управляющих символов.");
            if (password.Length < 8 || password.Length > 63 || password.Any(ch => ch < ' ' || ch > '~'))
                throw new WifiException("Пароль WPA2 должен содержать 8–63 печатных символа ASCII.");

            var client = open();
            try
            {
                var current = client.Read();
                if (current.State == PointState.Active)
                    throw new WifiException("Hosted Network уже запущена извне. Остановите её там, где запускали.");
                if (current.State == PointState.Unavailable)
                    throw new WifiException(current.Detail ?? "Hosted Network недоступна. Проверьте радио и драйвер.");
                if (previousRunning()) throw new WifiException("Появилась другая копия ПОМОГАТОРА Wi-Fi.");
                client.StartUsing(ssid, password);
                for (var attempt = 0; attempt < 12; attempt++)
                {
                    current = client.Read();
                    if (client.Interrupted) break;
                    if (current.State == PointState.Active && current.Adapter != Guid.Empty)
                    {
                        adapter = current.Adapter;
                        lease = client;
                        return;
                    }
                    if (attempt < 11) delay();
                }
                throw new WifiException("Windows приняла запуск, но не подтвердила активную точку и её виртуальный адаптер. " + current.Detail);
            }
            finally { if (lease != client) client.Dispose(); }
        }

        internal void Stop()
        {
            if (lease == null) return;
            try { lease.StopUsing(); }
            finally { Dispose(); }
        }

        public void Dispose()
        {
            var client = lease;
            lease = null;
            adapter = Guid.Empty;
            client?.Dispose();
        }
    }

    internal sealed class WifiException : Exception
    {
        internal WifiException(string message) : base(message) { }
    }
}
