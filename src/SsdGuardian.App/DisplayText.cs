using System.Text;
using SsdGuardian.Core;
using static SsdGuardian.App.UiText;

namespace SsdGuardian.App;

internal static class DisplayText
{
    private static readonly string[] EnglishByteUnits = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
    private static readonly string[] RussianByteUnits = ["Б", "КиБ", "МиБ", "ГиБ", "ТиБ", "ПиБ", "ЭиБ"];

    internal static string Bytes(double count)
    {
        var units = Language == UiLanguage.Russian ? RussianByteUnits : EnglishByteUnits;
        var unit = 0;
        while (count >= 1024 && unit < units.Length - 1)
        {
            count /= 1024;
            unit++;
        }

        return $"{count.ToString("0.##", Culture)} {units[unit]}";
    }

    internal static string Count(long value) => value.ToString("N0", Culture);
    internal static string Date(DateTimeOffset time) => time.ToLocalTime().ToString("g", Culture);
    internal static string Totals(WriteTotals totals) =>
        $"{Bytes(totals.Bytes)}, {Get("operations", "операций")} {Count(totals.Operations)}";

    internal static string PathName(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    internal static string Shorten(string text, int limit)
    {
        if (text.Length <= limit)
        {
            return text;
        }

        var kept = limit - 1;
        if (kept > 0 && char.IsHighSurrogate(text[kept - 1]))
        {
            kept--;
        }

        return string.Concat(text.AsSpan(0, kept), "…");
    }

    internal static string AnomalyText(Anomaly anomaly, string detailHeading)
    {
        var text = new StringBuilder();
        text.AppendLine(detailHeading);
        text.AppendLine();
        AppendProcess(text, anomaly.Process, anomaly.DiskNumber);
        text.AppendLine($"{Get("Detected at", "Время обнаружения")}: {Date(anomaly.Timestamp)}");
        text.AppendLine(string.Format(Culture, Get("Logical writes over {0} min: {1}", "Логическая запись за {0} мин: {1}"),
            anomaly.WindowMinutes, Totals(anomaly.Logical)));
        text.AppendLine($"{Get("Physical SSD writes over the same period", "Физическая запись SSD за тот же период")}: {Totals(anomaly.Physical)}");
        text.AppendLine($"{Get("Average logical write rate", "Средняя интенсивность логической записи")}: {Bytes(anomaly.BytesPerSecond)}{Get("/s", "/с")}");
        text.AppendLine($"{Get("Projected logical writes at this rate", "Прогноз логической записи при такой интенсивности")}: {Bytes(anomaly.ProjectedDailyBytes)}{Get("/day", "/сут")}");
        text.AppendLine($"{Get("Reason", "Причина")}: {anomaly.GetReason(Language)}");
        if (anomaly.IsElevated)
        {
            text.AppendLine(Get("The high write-rate threshold was exceeded.", "Превышен порог повышенной интенсивности."));
        }

        if (anomaly.IsNvidiaSignature)
        {
            text.AppendLine(Get("The NVIDIA diagnostic signature matched.", "Совпала диагностическая сигнатура NVIDIA."));
        }

        if (anomaly.IsRecurrence)
        {
            text.AppendLine(Get("The anomaly recurred after the process restarted.", "Аномалия появилась снова после перезапуска процесса."));
        }

        text.AppendLine();
        text.AppendLine(Get("Top files by logical writes", "Основные файлы логической записи"));
        if (anomaly.Files.Count == 0)
        {
            text.AppendLine(Get("File attribution is unavailable.", "Файлы достоверно не определены."));
        }

        foreach (var file in anomaly.Files)
        {
            var totals = file.Window ?? (anomaly.WindowMinutes switch
            {
                10 => file.Logical.Last10,
                30 => file.Logical.Last30,
                60 => file.Logical.Last60,
                _ => (WriteTotals?)null
            });
            text.AppendLine(totals is { } window ? $"{file.Path}  —  {Totals(window)}" : file.Path);
        }

        return text.ToString();
    }

    internal static string ProcessText(ProcessSnapshot row)
    {
        var text = new StringBuilder();
        AppendProcess(text, row.Process, row.DiskNumber);
        text.AppendLine();
        AppendWindows(text, row.Logical);
        text.AppendLine();
        text.AppendLine(Get("Top files by logical writes this session", "Основные файлы логической записи за сеанс"));
        if (row.Files.Count == 0)
        {
            text.AppendLine(Get("File attribution is unavailable.", "Файлы достоверно не определены."));
        }

        foreach (var file in row.Files.OrderByDescending(file => file.Logical.Session.Bytes))
        {
            text.AppendLine();
            text.AppendLine(file.Path);
            AppendWindows(text, file.Logical, indent: "  ");
        }

        return text.ToString();
    }

    internal static string DiskName(DiskInfo disk)
    {
        var name = disk.GetName(Language);
        if (!disk.IsConnected)
            name += Get(" (disconnected)", " (отключён)");
        if (!disk.IsTypeKnown)
            name += Get(" (type unknown)", " (тип неизвестен)");
        return name;
    }

    internal static string DiskText(DiskSnapshot row)
    {
        var text = new StringBuilder();
        var type = row.Disk.IsSsd ? "SSD" : Get("Disk", "Диск");
        text.AppendLine($"{type} {row.Disk.Number} — {DiskName(row.Disk)}");
        var volumes = row.Disk.Volumes.Count == 0 ? Get("Unknown", "Не определены") : string.Join(", ", row.Disk.Volumes);
        text.AppendLine($"{Get("Volumes", "Тома")}: {volumes}");
        text.AppendLine();
        if (!row.Disk.IsSsd)
        {
            text.AppendLine(Get("SSD write statistics require a confirmed SSD classification.", "Учёт записи требует подтверждённого типа SSD."));
            return text.ToString();
        }

        text.AppendLine(Get("Physical writes", "Физическая запись"));
        AppendWindows(text, row.Physical);
        text.AppendLine();
        text.AppendLine(Get("Logical file writes on SSD volumes", "Логическая запись в файлы на томах SSD"));
        AppendWindows(text, row.Logical);
        text.AppendLine($"{Get("Unattributed to a process this session", "Без подтверждённого процесса за сеанс")}: {Totals(row.UnattributedLogical.Session)}");
        return text.ToString();
    }

    private static void AppendWindows(StringBuilder text, WriteWindows windows, string indent = "")
    {
        text.AppendLine($"{indent}{Get("Last 10 minutes", "За 10 минут")}: {Totals(windows.Last10)}");
        text.AppendLine($"{indent}{Get("Last 30 minutes", "За 30 минут")}: {Totals(windows.Last30)}");
        text.AppendLine($"{indent}{Get("Last 60 minutes", "За 60 минут")}: {Totals(windows.Last60)}");
        text.AppendLine($"{indent}{Get("This session", "За сеанс")}: {Totals(windows.Session)}");
    }

    private static void AppendProcess(StringBuilder text, ProcessInfo process, int disk)
    {
        text.AppendLine($"{Get("Process", "Процесс")}: {process.Name} (PID {process.Identity.Pid})");
        text.AppendLine($"{Get("Started", "Создан")}: {Date(process.Identity.CreatedAt)}");
        text.AppendLine($"{Get("Path", "Путь")}: {process.Path ?? Get("Unavailable", "недоступен")}");
        text.AppendLine($"SSD: {Get("disk", "диск")} {disk}");
    }
}
