using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace PomogatorNetworkDiagnostics
{
    internal enum Outcome { Ok, Problem, Review }

    [DataContract]
    internal sealed class Finding
    {
        [DataMember(IsRequired = true)] public Outcome Status;
        [DataMember(IsRequired = true)] public string Summary;
        [DataMember(IsRequired = true)] public string[] Details;
        [DataMember(IsRequired = true)] public string[] Codes;
        [DataMember(EmitDefaultValue = false)] public string Id;
        [DataMember(EmitDefaultValue = false)] public string Title;
        [DataMember(EmitDefaultValue = false)] public string CheckedAt;
        [DataMember(EmitDefaultValue = false)] public string Label;
        [DataMember(EmitDefaultValue = false)] public long DurationMs;

        internal static Finding Create(Outcome status, string summary,
            IEnumerable<string> details = null, IEnumerable<string> codes = null)
        {
            return new Finding { Status = status, Summary = summary,
                Details = details?.ToArray() ?? Array.Empty<string>(),
                Codes = codes?.ToArray() ?? Array.Empty<string>() };
        }

        internal static string LabelFor(Outcome status)
        {
            switch (status)
            {
                case Outcome.Ok: return "Исправно";
                case Outcome.Problem: return "Обнаружена проблема";
                default: return "Требуется проверка";
            }
        }

        internal static string Hex(int code) => "0x" + code.ToString("X8", CultureInfo.InvariantCulture);
    }

    [DataContract]
    internal sealed class Interpretation
    {
        [DataMember] public string[] Observed;
        [DataMember] public string[] Possible;
        [DataMember] public string[] Unverified;

        internal static Interpretation Analyze(Finding[] results)
        {
            bool Is(string id, Outcome status) => results.Any(result => result.Id == id && result.Status == status);
            var observed = new List<string>();
            var possible = new List<string>();
            var unverified = new List<string> {
                "Передача трафика без VPN, работа VPN-расширений и отрисовка страниц в браузере не подтверждаются этими проверками.",
                "Проверяются контрольные адреса. Доступность конкретного сайта и причина его зависания могут отличаться."
            };
            foreach (var result in results.Where(item => item.Status == Outcome.Problem))
                observed.Add(result.Title + " — " + result.Summary);
            if (results.Any(result => result.Id == "ics" && result.Codes.Contains("0x80004002")))
                observed.Add("При чтении объекта ICS получен E_NOINTERFACE. Сам по себе этот код описывает сбой COM-вызова.");
            if (Is("ip", Outcome.Ok) && Is("dns", Outcome.Problem))
                possible.Add("TCP по IP работает, а контрольные имена не разрешились. Вероятная область проверки — системное разрешение имён или правила VPN для DNS.");
            if (Is("direct", Outcome.Ok) && Is("system-proxy", Outcome.Problem))
                possible.Add("Прямые HTTP(S)-запросы прошли, запросы через системный прокси не получили ожидаемого ответа. Проверьте доступность и правила прокси.");
            if (Is("direct", Outcome.Ok))
                unverified.Add("При успешных контрольных запросах чёрный экран или зависание могут быть связаны с конкретным сайтом или браузером. Журналы и расширения браузера не читаются.");
            return new Interpretation { Observed = observed.ToArray(), Possible = possible.ToArray(), Unverified = unverified.ToArray() };
        }
    }

    [DataContract]
    internal sealed class Report
    {
        [DataMember] public string Version = "0.1.0";
        [DataMember] public string StartedAt;
        [DataMember] public string FinishedAt;
        [DataMember] public string WindowsVersion;
        [DataMember] public Finding[] Results;
        [DataMember] public Interpretation Conclusions;

        internal const string Caveat = "Статус относится к конкретной проверке. Подтверждённый сбой контрольного запроса не определяет его причину. " +
            "Ошибка COM и состояние «Неопознанная сеть» сами по себе не означают, что интернет не работает.";
        internal const string ExportWarning = "Отчёт может содержать персональные сетевые данные: IP-адреса, адреса DNS, маршруты, " +
            "имена адаптеров и GUID. Проверьте файл перед передачей другому человеку.";

        internal string ToText()
        {
            var text = new StringBuilder();
            text.AppendLine("ПОМОГАТОР — Диагностика сети v" + Version);
            text.AppendLine("Начало: " + StartedAt);
            text.AppendLine("Завершение: " + FinishedAt);
            text.AppendLine("Версия Windows: " + WindowsVersion);
            text.AppendLine(Caveat);
            text.AppendLine(ExportWarning);
            if (Conclusions != null)
            {
                AppendGroup(text, "Подтверждённые сбои проверок", Conclusions.Observed);
                AppendGroup(text, "Вероятные причины или области проверки", Conclusions.Possible);
                AppendGroup(text, "Требуется проверка", Conclusions.Unverified);
            }
            foreach (var result in Results)
            {
                text.AppendLine();
                text.AppendLine("[" + result.Label + "] " + result.Title);
                text.AppendLine(result.Summary);
                text.AppendLine("Проверено: " + result.CheckedAt + " | " + result.DurationMs + " мс");
                if (result.Codes.Length != 0) text.AppendLine("Коды: " + string.Join(", ", result.Codes));
                foreach (var detail in result.Details) text.AppendLine("  " + detail);
            }
            return text.ToString();
        }

        private static void AppendGroup(StringBuilder text, string heading, string[] items)
        {
            text.AppendLine();
            text.AppendLine(heading);
            if (items.Length == 0) text.AppendLine("  Нет по собранным данным.");
            foreach (var item in items) text.AppendLine("  • " + item);
        }

        internal static string Now() => DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);

        internal static string Json<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        internal static T Parse<T>(string json)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }
    }
}
