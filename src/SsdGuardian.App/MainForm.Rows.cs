using SsdGuardian.Core;
using SsdGuardian.Windows;
using static SsdGuardian.App.UiText;

namespace SsdGuardian.App;

internal sealed partial class MainForm
{
    private int? _processSortColumn;
    private bool _processSortDescending;

    private void UpdateSnapshot(MonitorSnapshot snapshot)
    {
        var unavailable = CurrentStatus().State is MonitorState.Starting or MonitorState.Unavailable;
        var hasSessionData = !unavailable || snapshot.SessionPhysical.Operations > 0;
        var physical = hasSessionData ? DisplayText.Totals(snapshot.SessionPhysical) : Get("Unavailable", "нет данных");
        var lowerBound = hasSessionData && snapshot.LostEvents > 0 ? Get("at least ", "не менее ") : "";
        _summaryLabel.Text = $"{Get("Physical SSD writes this session", "Физическая запись на SSD за сеанс")}: {lowerBound}{physical}" +
            $"  |  {Get("Anomalies", "Аномалий")}: {DisplayText.Count(snapshot.AnomalyCount)}";

        var quality = new List<string>();
        if (snapshot.LostEvents > 0)
        {
            var suffix = snapshot.IsReliable
                ? Get("session total incomplete", "итог сеанса неполный")
                : Get("rolling windows incomplete", "скользящие окна неполные");
            quality.Add($"{Get("Lost ETW events", "Потеряно событий ETW")}: {DisplayText.Count(snapshot.LostEvents)} ({suffix})");
        }
        else if (!snapshot.IsReliable)
        {
            quality.Add(Get("Rolling windows include a monitoring gap.", "Скользящие окна содержат пропуск наблюдения."));
        }

        if (snapshot.UnmappedPhysical.Operations > 0 || snapshot.UnmappedLogical.Operations > 0)
        {
            quality.Add(string.Format(Culture, Get("Unmapped operations: {0} physical, {1} file", "Без привязки: физических {0}, файловых {1}"),
                DisplayText.Count(snapshot.UnmappedPhysical.Operations), DisplayText.Count(snapshot.UnmappedLogical.Operations)));
        }

        if (snapshot.UntrackedProcesses > 0 || snapshot.UntrackedFiles > 0)
        {
            quality.Add(string.Format(Culture, Get("Events beyond detail limits: {0} process, {1} file", "Событий вне лимита детализации: процессов {0}, файлов {1}"),
                DisplayText.Count(snapshot.UntrackedProcesses), DisplayText.Count(snapshot.UntrackedFiles)));
        }

        _qualityLabel.Text = string.Join("  |  ", quality);

        UpdateProcessRows(snapshot.Processes);

        var disks = snapshot.Disks.Where(row => row.Disk.IsSsd || !row.Disk.IsTypeKnown)
            .OrderByDescending(row => row.Disk.IsConnected).ThenBy(row => row.Disk.Number);
        ReplaceRows(_diskTable, disks.Select(MakeDiskRow), tag => (tag as DiskSnapshot)?.Disk.ObservationId);
    }

    private void ChangeProcessSort(int column)
    {
        if (_processSortColumn != column)
        {
            _processSortColumn = column;
            _processSortDescending = true;
        }
        else if (_processSortDescending)
        {
            _processSortDescending = false;
        }
        else
        {
            _processSortColumn = null;
        }

        SetProcessColumnText();
        UpdateProcessRows(_engine.Snapshot(DateTimeOffset.UtcNow).Processes, preserveTop: false);
    }

    private void SetProcessColumnText()
    {
        string[] captions =
        [
            Get("Process", "Процесс"), "PID", "SSD",
            Get("10 min", "10 мин"), Get("30 min", "30 мин"), Get("60 min", "60 мин"),
            Get("Session", "За сеанс"), Get("Operations", "Операций"), Get("Top file", "Основной файл")
        ];
        if (_processSortColumn is int column)
        {
            captions[column] += _processSortDescending ? " ↓" : " ↑";
        }

        SetColumnText(_processTable, captions);
    }

    private void UpdateProcessRows(IReadOnlyList<ProcessSnapshot> snapshots, bool preserveTop = true)
    {
        var rows = snapshots.Select(row => new ProcessRow(row, GetTopFileName(row)));
        var byRecentWrites = rows
            .OrderByDescending(row => row.Snapshot.Logical.Last10.Bytes)
            .ThenByDescending(row => row.Snapshot.Logical.Last30.Bytes)
            .ThenByDescending(row => row.Snapshot.Logical.Session.Bytes);
        var textOrder = StringComparer.Create(Culture, ignoreCase: true);
        var ordered = _processSortColumn switch
        {
            0 => SortProcessRows(byRecentWrites, row => row.Snapshot.Process.Name, textOrder),
            1 => SortProcessRows(byRecentWrites, row => row.Snapshot.Process.Identity.Pid),
            2 => SortProcessRows(byRecentWrites, row => row.Snapshot.DiskNumber),
            3 => SortProcessRows(byRecentWrites, row => row.Snapshot.Logical.Last10.Bytes),
            4 => SortProcessRows(byRecentWrites, row => row.Snapshot.Logical.Last30.Bytes),
            5 => SortProcessRows(byRecentWrites, row => row.Snapshot.Logical.Last60.Bytes),
            6 => SortProcessRows(byRecentWrites, row => row.Snapshot.Logical.Session.Bytes),
            7 => SortProcessRows(byRecentWrites, row => row.Snapshot.Logical.Session.Operations),
            8 => SortProcessRows(byRecentWrites, row => row.TopFileName ?? Get("Unknown", "не определён"), textOrder),
            _ => byRecentWrites
        };
        ReplaceRows(_processTable, ordered.Select(MakeProcessRow),
            tag => tag is ProcessSnapshot row ? (row.Process.Identity, row.DiskNumber) : null, preserveTop);
    }

    private IOrderedEnumerable<ProcessRow> SortProcessRows<TKey>(IEnumerable<ProcessRow> rows,
        Func<ProcessRow, TKey> value, IComparer<TKey>? comparer = null) => _processSortDescending
        ? rows.OrderByDescending(value, comparer)
        : rows.OrderBy(value, comparer);

    private static string? GetTopFileName(ProcessSnapshot row)
    {
        var topFile = row.Files.OrderByDescending(file => file.Logical.Last10.Bytes)
            .ThenByDescending(file => file.Logical.Session.Bytes).FirstOrDefault();
        return topFile is null ? null : DisplayText.PathName(topFile.Path);
    }

    private static ListViewItem MakeProcessRow(ProcessRow processRow)
    {
        var row = processRow.Snapshot;
        return new ListViewItem([
            row.Process.Name,
            row.Process.Identity.Pid.ToString(),
            row.DiskNumber.ToString(),
            DisplayText.Bytes(row.Logical.Last10.Bytes),
            DisplayText.Bytes(row.Logical.Last30.Bytes),
            DisplayText.Bytes(row.Logical.Last60.Bytes),
            DisplayText.Bytes(row.Logical.Session.Bytes),
            DisplayText.Count(row.Logical.Session.Operations),
            processRow.TopFileName ?? Get("Unknown", "не определён")
        ]) { Tag = row };
    }

    private static ListViewItem MakeDiskRow(DiskSnapshot row) => new([
        row.Disk.Number.ToString(),
        DisplayText.DiskName(row.Disk),
        row.Disk.Volumes.Count == 0 ? Get("Unknown", "не определены") : string.Join(", ", row.Disk.Volumes),
        row.Disk.IsSsd ? DisplayText.Bytes(row.Physical.Last10.Bytes) : "—",
        row.Disk.IsSsd ? DisplayText.Bytes(row.Physical.Last30.Bytes) : "—",
        row.Disk.IsSsd ? DisplayText.Bytes(row.Physical.Last60.Bytes) : "—",
        row.Disk.IsSsd ? DisplayText.Bytes(row.Physical.Session.Bytes) : "—",
        row.Disk.IsSsd ? DisplayText.Count(row.Physical.Session.Operations) : "—"
    ]) { Tag = row };

    private ListViewItem MakeAnomalyRow(Anomaly anomaly) => new([
        DisplayText.Date(anomaly.Timestamp),
        anomaly.Process.Name,
        anomaly.DiskNumber.ToString(),
        DisplayText.Bytes(anomaly.Logical.Bytes),
        $"{anomaly.WindowMinutes} {Get("min", "мин")}",
        _liveAnomalies.Contains(anomaly) ? Get("Active", "Активна") : Get("History", "История"),
        anomaly.GetReason(Language)
    ]) { Tag = anomaly };

    private void UpdateHistory()
    {
        _anomaliesMenuItem.Enabled = _liveAnomalies.Count > 0 || _history.Count > 0;
        if (!_layoutReady)
        {
            return;
        }

        var rows = _liveAnomalies.OrderByDescending(anomaly => anomaly.Timestamp).Union(_history);
        ReplaceRows(_historyTable, rows.Select(MakeAnomalyRow), tag => tag);
    }

    private static void ReplaceRows(ListView table, IEnumerable<ListViewItem> rows, Func<object?, object?> key, bool preserveTop = true)
    {
        var selected = table.SelectedItems.Count > 0 ? key(table.SelectedItems[0].Tag) : null;
        var top = preserveTop && table.TopItem is { } topRow ? key(topRow.Tag) : null;
        var items = rows.ToArray();
        table.BeginUpdate();
        try
        {
            table.Items.Clear();
            table.Items.AddRange(items);
            if (selected is not null && items.FirstOrDefault(item => Equals(selected, key(item.Tag))) is { } match)
            {
                match.Selected = true;
                match.Focused = true;
            }

            if (top is not null && items.FirstOrDefault(item => Equals(top, key(item.Tag))) is { } first)
            {
                table.TopItem = first;
            }
        }
        finally
        {
            table.EndUpdate();
        }
    }

    private void OpenSelectedProcess()
    {
        if (_processTable.SelectedItems.Count > 0 && _processTable.SelectedItems[0].Tag is ProcessSnapshot row)
        {
            using var detail = new DetailForm(row.Process.Name, DisplayText.ProcessText(row));
            detail.ShowDialog(this);
        }
    }

    private void OpenSelectedDisk()
    {
        if (_diskTable.SelectedItems.Count == 0 || _diskTable.SelectedItems[0].Tag is not DiskSnapshot row)
        {
            return;
        }

        var title = $"{Get("Disk", "Диск")} {row.Disk.Number}";
        using var detail = new DetailForm(title, DisplayText.DiskText(row));
        detail.ShowDialog(this);
    }

    private readonly record struct ProcessRow(ProcessSnapshot Snapshot, string? TopFileName);
}
