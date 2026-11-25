using SsdGuardian.Core;
using SsdGuardian.Windows;
using static SsdGuardian.App.UiText;

namespace SsdGuardian.App;

internal sealed partial class MainForm
{
    private void RecordAnomalies(IReadOnlyList<Anomaly> anomalies)
    {
        foreach (var anomaly in anomalies)
        {
            _history.Insert(0, anomaly);
            _liveAnomalies.RemoveWhere(item => item.Process.Identity == anomaly.Process.Identity && item.DiskNumber == anomaly.DiskNumber);
            _liveAnomalies.Add(anomaly);
        }

        while (_history.Count > ApplicationStore.RetainedEntries)
        {
            _history.RemoveAt(_history.Count - 1);
        }

        foreach (var excess in _liveAnomalies.OrderByDescending(anomaly => anomaly.Timestamp).Skip(ActiveAnomalyLimit).ToArray())
        {
            _liveAnomalies.Remove(excess);
        }

        WriteEntries(HistoryFile, _history.Select(CompactHistory).ToArray());
        UpdateHistory();
        var notice = anomalies.OrderByDescending(anomaly => anomaly.IsRecurrence)
            .ThenByDescending(anomaly => anomaly.BytesPerSecond).First();
        Notify(notice, demo: false, additional: anomalies.Count - 1);
    }

    private static Anomaly CompactHistory(Anomaly anomaly) => anomaly with
    {
        Process = anomaly.Process with
        {
            Name = DisplayText.Shorten(anomaly.Process.Name, 100),
            Path = anomaly.Process.Path is null ? null : DisplayText.Shorten(anomaly.Process.Path, 360)
        },
        Reason = DisplayText.Shorten(anomaly.Reason, 300),
        ReasonRussian = anomaly.ReasonRussian is null ? null : DisplayText.Shorten(anomaly.ReasonRussian, 300),
        Files = anomaly.Files.Take(3).Select(file => file with { Path = DisplayText.Shorten(file.Path, 360) }).ToArray()
    };

    private void Notify(Anomaly anomaly, bool demo, int additional = 0)
    {
        _latestAnomaly = additional > 0 ? null : anomaly;
        _latestIsDemo = demo;

        var title = demo
            ? Get("SSD Guardian — demo", "SSD Guardian — демо")
            : anomaly.IsRecurrence
                ? Get("SSD Guardian — recurring anomaly", "SSD Guardian — повторная аномалия")
                : Get("SSD Guardian — write anomaly", "SSD Guardian — аномалия записи");
        var filename = anomaly.Files.Count > 0 ? DisplayText.PathName(anomaly.Files[0].Path) : Get("unknown", "не определён");
        var description = string.Format(Culture,
            Get("{0}: {1} in {2} min, {3}/s, projected {4}/day.\nFile: {5}.\n",
                "{0}: {1} за {2} мин, {3}/с, прогноз {4}/сут.\nФайл: {5}.\n"),
            DisplayText.Shorten(anomaly.Process.Name, 42), DisplayText.Bytes(anomaly.Logical.Bytes), anomaly.WindowMinutes,
            DisplayText.Bytes(anomaly.BytesPerSecond), DisplayText.Bytes(anomaly.ProjectedDailyBytes), DisplayText.Shorten(filename, 38));
        var tail = additional > 0
            ? $"\n{Get("Other anomalies", "Других аномалий")}: {DisplayText.Count(additional)}. {Get("Open list", "Открыть список")}"
            : "";
        var reason = DisplayText.Shorten(anomaly.GetReason(Language), Math.Max(1, 255 - description.Length - tail.Length));
        _tray.ShowBalloonTip(7000, title, DisplayText.Shorten(description + reason + tail, 255), ToolTipIcon.Warning);
    }

    private void ShowDemo(bool openDetails = true)
    {
        var anomaly = Anomaly.CreateDemo(DateTimeOffset.UtcNow);
        Notify(anomaly, demo: true);
        if (openDetails)
        {
            ShowAnomaly(anomaly, demo: true, allowActions: false);
        }
    }

    private void OpenNotification()
    {
        if (_latestAnomaly is { } anomaly)
        {
            ShowAnomaly(anomaly, _latestIsDemo, _liveAnomalies.Contains(anomaly));
            return;
        }

        ShowAnomalyList();
    }

    private void ShowAnomalyList()
    {
        BuildLayout();
        _tabs.SelectedTab = _historyTab;
        ShowWindow();
    }

    private void OpenSelectedAnomaly()
    {
        if (_historyTable.SelectedItems.Count > 0 && _historyTable.SelectedItems[0].Tag is Anomaly anomaly)
        {
            ShowAnomaly(anomaly, demo: false, allowActions: _liveAnomalies.Contains(anomaly));
        }
    }

    private void ShowAnomaly(Anomaly anomaly, bool demo, bool allowActions)
    {
        if (_openAnomaly is not null)
        {
            _openAnomaly.Activate();
            return;
        }

        var title = demo
            ? Get("Demo notification", "Демонстрационное уведомление")
            : Get("Suspicious write activity", "Подозрительная активность записи");
        var text = DisplayText.AnomalyText(anomaly, title);
        var actions = new List<(string Caption, EventHandler Click)>();
        if (allowActions)
        {
            actions.Add((Get("Ignore for one hour", "Игнорировать на час"), (_, _) =>
            {
                _engine.Snooze(anomaly.Process.Identity, DateTimeOffset.UtcNow);
                ResolveAnomaly(anomaly);
            }));
            actions.Add((Get("End process…", "Завершить процесс…"), (_, _) => ConfirmTermination(anomaly)));
        }

        using var detail = new DetailForm(title, text, actions.ToArray());
        _openAnomaly = detail;
        try
        {
            if (Visible)
            {
                detail.ShowDialog(this);
            }
            else
            {
                detail.ShowDialog();
            }
        }
        finally
        {
            _openAnomaly = null;
        }
    }

    private void ConfirmTermination(Anomaly anomaly)
    {
        var process = anomaly.Process;
        var title = Get("End process", "Завершение процесса");
        var prompt = string.Format(Culture,
            Get("End {0} (PID {1})? The process may lose unsaved data.",
                "Завершить {0} (PID {1})? Несохранённые данные процесса могут быть потеряны."),
            process.Name, process.Identity.Pid);
        var choice = MessageBox.Show(_openAnomaly!,
            prompt, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (choice != DialogResult.Yes)
        {
            return;
        }

        var requestedAt = DateTimeOffset.UtcNow;
        var result = ProcessTerminator.Terminate(process.Identity);
        if (result.Success)
        {
            _engine.MarkTerminated(process.Identity, process.Path, requestedAt);
            WriteEntries(ActionsFile, _engine.ExportTerminations(DateTimeOffset.UtcNow));
        }

        Log($"End process {process.Name} (PID {process.Identity.Pid}). {result.GetMessage(UiLanguage.English)}",
            $"Завершение процесса {process.Name} (PID {process.Identity.Pid}). {result.GetMessage(UiLanguage.Russian)}");
        MessageBox.Show(_openAnomaly!, result.GetMessage(Language), title, MessageBoxButtons.OK,
            result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        if (result.Success)
        {
            ResolveAnomaly(anomaly);
        }
    }

    private void ResolveAnomaly(Anomaly anomaly)
    {
        _liveAnomalies.RemoveWhere(item => item.Process.Identity == anomaly.Process.Identity);
        UpdateHistory();
        UpdateStatus(CurrentStatus());
        _openAnomaly?.Close();
    }
}
