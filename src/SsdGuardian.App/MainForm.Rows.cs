using SsdGuardian.Core;
using SsdGuardian.Windows;
using static SsdGuardian.App.UiText;

namespace SsdGuardian.App;

internal sealed partial class MainForm
{
    private int? _processSortColumn;
    private bool _processSortDescending;
    private MonitorSnapshot? _latestSnapshot;
    private ProcessRow[] _processRows = [];
    private ProgramRow[] _programRows = [];

    private void UpdateSnapshot(MonitorSnapshot snapshot)
    {
        _latestSnapshot = snapshot;
        var hasSessionData = HasSessionData(snapshot);
        var lowerBound = snapshot.HasObservationGap ? Get("at least ", "не менее ") : "";
        string Total(WriteTotals totals) => hasSessionData ? lowerBound + DisplayText.Totals(totals) : Get("Unavailable", "нет данных");
        _summaryLabel.Text = $"{Get("Physical SSD writes this session", "Физическая запись на SSD за сеанс")}: {Total(snapshot.SessionPhysical)}" +
            $"  |  {Get("Anomalies", "Аномалий")}: {DisplayText.Count(snapshot.AnomalyCount)}";
        _logicalSummaryLabel.Text = $"{Get("Logical file writes on confirmed SSDs this session", "Логическая запись на подтверждённых SSD за сеанс")}: {Total(snapshot.SessionLedger.KnownLogical)}";

        var quality = new List<string>();
        if (snapshot.LostEvents > 0)
        {
            var suffix = snapshot.IsReliable
                ? Get("session total incomplete", "итог сеанса неполный")
                : Get("rolling windows incomplete", "скользящие окна неполные");
            quality.Add($"{Get("Lost ETW events", "Потеряно событий ETW")}: {DisplayText.Count(snapshot.LostEvents)} ({suffix})");
        }
        else if (snapshot.HasObservationGap)
        {
            quality.Add(snapshot.IsReliable
                ? Get("Session totals include a monitoring gap.", "В итогах сеанса есть пропуск наблюдения.")
                : Get("Session totals and rolling windows include a monitoring gap.", "В итогах сеанса и скользящих окнах есть пропуск наблюдения."));
        }

        if (snapshot.UntrackedProcesses > 0 || snapshot.UntrackedFiles > 0)
        {
            quality.Add(string.Format(Culture, Get("Events beyond detail limits: {0} process, {1} file", "Событий вне лимита детализации: процессов {0}, файлов {1}"),
                DisplayText.Count(snapshot.UntrackedProcesses), DisplayText.Count(snapshot.UntrackedFiles)));
        }

        _qualityLabel.Text = string.Join("  |  ", quality);

        UpdateSelectedSnapshotTable(snapshot);
    }

    private bool HasSessionData(MonitorSnapshot snapshot) =>
        CurrentStatus().State is not (MonitorState.Starting or MonitorState.Unavailable) ||
        snapshot.SessionPhysical.Operations > 0 || snapshot.SessionLedger.KnownLogical.Operations > 0 ||
        snapshot.UnmappedLogical.Operations > 0 || snapshot.UnmappedPhysical.Operations > 0;

    private void UpdateSelectedSnapshotTable(MonitorSnapshot snapshot)
    {
        if (_tabs.SelectedTab == _processTab)
        {
            UpdateProcessRows(snapshot.SessionLedger.Processes);
        }
        else if (_tabs.SelectedTab == _sessionTab)
        {
            UpdateSessionRows(snapshot);
        }
        else if (_tabs.SelectedTab == _programTab)
        {
            UpdateProgramRows(snapshot.SessionLedger, HasSessionData(snapshot));
        }
        else if (_tabs.SelectedTab == _diskTab)
        {
            var disks = snapshot.Disks.Where(row => row.Disk.IsSsd || !row.Disk.IsTypeKnown)
                .OrderByDescending(row => row.Disk.IsConnected).ThenBy(row => row.Disk.Number);
            ReplaceRows(_diskTable, disks.Select(MakeDiskRow), tag => (tag as DiskSnapshot)?.Disk.ObservationId);
        }
    }

    private void UpdateSessionRows(MonitorSnapshot snapshot)
    {
        var ledger = snapshot.SessionLedger;
        var hasSessionData = HasSessionData(snapshot);
        ListViewItem Row(string name, WriteTotals totals) => new([
            name,
            hasSessionData ? DisplayText.Count(totals.Bytes) : "—",
            hasSessionData ? DisplayText.Count(totals.Operations) : "—"
        ]) { Tag = name };
        const string indent = "    ";

        ReplaceRows(_sessionTable,
        [
            Row(Get("Logical writes on confirmed SSDs", "Логическая запись на подтверждённых SSD"), ledger.KnownLogical),
            Row(indent + Get("Listed processes with no confirmed exit", "Процессы в списке без подтверждённого завершения"), ledger.ActiveLogical),
            Row(indent + Get("Listed processes with confirmed exit", "Завершённые процессы в списке"), ledger.ExitedLogical),
            Row(indent + Get("Other identified processes", "Другие опознанные процессы"), ledger.OtherLogical),
            Row(indent + Get("Process not identified", "Процесс не определён"), ledger.UnidentifiedLogical),
            Row(Get("Logical writes on undetermined drives", "Логическая запись без привязки к SSD"), snapshot.UnmappedLogical),
            Row(Get("Physical writes on confirmed SSDs", "Физическая запись на подтверждённых SSD"), snapshot.SessionPhysical),
            Row(Get("Physical writes on undetermined drives", "Физическая запись без привязки к SSD"), snapshot.UnmappedPhysical)
        ], tag => tag);
    }

    private void UpdateProgramRows(SessionLedgerSnapshot ledger, bool hasSessionData)
    {
        var known = ledger.Programs.OrderByDescending(row => row.Logical.Bytes)
            .ThenBy(row => row.Path, StringComparer.OrdinalIgnoreCase).Select(row => new ProgramRow(row.Name, row.Logical, row));
        ProgramRow OtherRow(string name, WriteTotals totals) => new(name, hasSessionData ? totals : null, null);
        ProgramRow[] coverage =
        [
            OtherRow(Get("Executable path not verified", "Путь программы не подтверждён"), ledger.UnidentifiedProgramLogical),
            OtherRow(Get("Other identified processes", "Другие опознанные процессы"), ledger.OtherLogical),
            OtherRow(Get("Process not identified", "Процесс не определён"), ledger.UnidentifiedLogical)
        ];
        var rows = coverage.Concat(known).ToArray();
        ReplaceVirtualRows(_programTable, ref _programRows, rows, row => row.Snapshot?.Path ?? row.Name,
            comparer: StringComparer.OrdinalIgnoreCase);
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
        if (_latestSnapshot is { } snapshot)
        {
            UpdateProcessRows(snapshot.SessionLedger.Processes, preserveTop: false);
        }
    }

    private void SetProcessColumnText()
    {
        string[] captions =
        [
            Get("Process", "Процесс"), "PID", "SSD", Get("Exit status", "Завершение"),
            Get("10 min", "10 мин"), Get("30 min", "30 мин"), Get("60 min", "60 мин"),
            Get("Session", "За сеанс"), Get("Operations", "Операций"), Get("Top file", "Основной файл")
        ];
        if (_processSortColumn is int column)
        {
            captions[column] += _processSortDescending ? " ↓" : " ↑";
        }

        SetColumnText(_processTable, captions);
    }

    private void UpdateProcessRows(IReadOnlyList<ProcessSessionSnapshot> snapshots, bool preserveTop = true)
    {
        var rows = snapshots.Select(row => new ProcessRow(row, GetTopFileName(row)));
        var byRecentWrites = rows
            .OrderByDescending(row => row.Snapshot.HasRollingDetails ? row.Snapshot.Logical.Last10.Bytes : 0)
            .ThenByDescending(row => row.Snapshot.HasRollingDetails ? row.Snapshot.Logical.Last30.Bytes : 0)
            .ThenByDescending(row => row.Snapshot.Logical.Session.Bytes);
        var textOrder = StringComparer.Create(Culture, ignoreCase: true);
        var ordered = _processSortColumn switch
        {
            0 => SortProcessRows(byRecentWrites, row => row.Snapshot.Process.Name, textOrder),
            1 => SortProcessRows(byRecentWrites, row => row.Snapshot.Process.Identity.Pid),
            2 => SortProcessRows(byRecentWrites, row => row.Snapshot.DiskNumber),
            3 => SortProcessRows(byRecentWrites, row => DisplayText.ExitStatus(row.Snapshot.ExitTime), textOrder),
            4 => SortProcessRows(byRecentWrites, row => row.Snapshot.Logical.Last10.Bytes, unavailable: MissingWindows),
            5 => SortProcessRows(byRecentWrites, row => row.Snapshot.Logical.Last30.Bytes, unavailable: MissingWindows),
            6 => SortProcessRows(byRecentWrites, row => row.Snapshot.Logical.Last60.Bytes, unavailable: MissingWindows),
            7 => SortProcessRows(byRecentWrites, row => row.Snapshot.Logical.Session.Bytes),
            8 => SortProcessRows(byRecentWrites, row => row.Snapshot.Logical.Session.Operations),
            9 => SortProcessRows(byRecentWrites, row => row.TopFileName ?? "", textOrder, row => row.TopFileName is null),
            _ => byRecentWrites
        };
        ReplaceVirtualRows(_processTable, ref _processRows, ordered.ToArray(),
            row => (row.Snapshot.Process.Identity, row.Snapshot.DiskObservationId), preserveTop);

        static bool MissingWindows(ProcessRow row) => !row.Snapshot.HasRollingDetails;
    }

    private IEnumerable<ProcessRow> SortProcessRows<TKey>(IEnumerable<ProcessRow> rows,
        Func<ProcessRow, TKey> value, IComparer<TKey>? comparer = null, Func<ProcessRow, bool>? unavailable = null)
    {
        var sorted = _processSortDescending ? rows.OrderByDescending(value, comparer) : rows.OrderBy(value, comparer);
        return unavailable is null ? sorted : sorted.OrderBy(unavailable);
    }

    private static string? GetTopFileName(ProcessSessionSnapshot row)
    {
        var topFile = row.Files.MaxBy(file => (file.Logical.Last10.Bytes, file.Logical.Session.Bytes));
        return topFile is null ? null : DisplayText.PathName(topFile.Path);
    }

    private static ListViewItem MakeProcessRow(ProcessRow processRow)
    {
        var row = processRow.Snapshot;
        string RecentBytes(WriteTotals totals) => row.HasRollingDetails
            ? DisplayText.Amount(DisplayText.Bytes(totals.Bytes), row.AreDetailsPartial)
            : "—";
        return new ListViewItem([
            row.Process.Name,
            row.Process.Identity.Pid.ToString(),
            row.IsDiskConnected ? row.DiskNumber.ToString() : $"{row.DiskNumber} {Get("(offline)", "(откл.)")}",
            DisplayText.ExitStatus(row.ExitTime),
            RecentBytes(row.Logical.Last10),
            RecentBytes(row.Logical.Last30),
            RecentBytes(row.Logical.Last60),
            DisplayText.Amount(DisplayText.Bytes(row.Logical.Session.Bytes), row.IsPartial),
            DisplayText.Amount(DisplayText.Count(row.Logical.Session.Operations), row.IsPartial),
            !row.HasRollingDetails ? "—" : processRow.TopFileName ?? Get("Unknown", "не определён")
        ]) { Tag = row };
    }

    private static ListViewItem MakeProgramRow(ProgramRow row)
    {
        var program = row.Snapshot;
        var partial = program?.IsPartial ?? false;
        return new([
            row.Name,
            row.Totals is { } totals ? DisplayText.Amount(DisplayText.Bytes(totals.Bytes), partial) : "—",
            row.Totals is { } recorded ? DisplayText.Amount(DisplayText.Count(recorded.Operations), partial) : "—",
            program is null ? "—" : DisplayText.Amount(DisplayText.Count(program.Instances), partial),
            program is null ? "—" : DisplayText.Amount(DisplayText.Count(program.ExitedInstances), partial),
            program?.Path ?? "—"
        ]) { Tag = program, ToolTipText = program?.Path ?? "" };
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

    private static void ReplaceVirtualRows<TRow, TKey>(ListView table, ref TRow[] currentRows, TRow[] nextRows,
        Func<TRow, TKey> key, bool preserveTop = true, IEqualityComparer<TKey>? comparer = null)
    {
        comparer ??= EqualityComparer<TKey>.Default;
        var selected = table.SelectedIndices.Count > 0 ? table.SelectedIndices[0] : -1;
        var top = preserveTop && currentRows.Length > 0 ? table.TopItem?.Index ?? -1 : -1;
        var nextSelected = FindNextRow(currentRows, selected);
        var nextTop = FindNextRow(currentRows, top);

        table.BeginUpdate();
        try
        {
            table.SelectedIndices.Clear();
            currentRows = nextRows;
            table.VirtualListSize = nextRows.Length;
            if (nextSelected >= 0)
            {
                table.SelectedIndices.Add(nextSelected);
                table.Items[nextSelected].Focused = true;
            }

            if (nextRows.Length > 0 && table.IsHandleCreated)
            {
                table.TopItem = table.Items[Math.Max(0, nextTop)];
            }

            table.Invalidate();
        }
        finally
        {
            table.EndUpdate();
        }

        int FindNextRow(TRow[] previous, int index)
        {
            if (index < 0 || index >= previous.Length)
            {
                return -1;
            }

            var rowKey = key(previous[index]);
            return Array.FindIndex(nextRows, row => comparer.Equals(rowKey, key(row)));
        }
    }

    private void OpenSelectedProcess()
    {
        if (_processTable.SelectedIndices.Count > 0)
        {
            var row = _processRows[_processTable.SelectedIndices[0]].Snapshot;
            using var detail = new DetailForm(row.Process.Name, DisplayText.ProcessText(row));
            detail.ShowDialog(this);
        }
    }

    private void OpenSelectedProgram()
    {
        if (_programTable.SelectedIndices.Count > 0 && _programRows[_programTable.SelectedIndices[0]].Snapshot is { } row)
        {
            using var detail = new DetailForm(row.Name, DisplayText.ProgramText(row));
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

    private readonly record struct ProcessRow(ProcessSessionSnapshot Snapshot, string? TopFileName);
    private readonly record struct ProgramRow(string Name, WriteTotals? Totals, ProgramSessionSnapshot? Snapshot);
}
