using SsdGuardian.Core;
using SsdGuardian.Windows;

namespace SsdGuardian.App;

internal sealed partial class MainForm : Form
{
    private const string HistoryFile = "anomalies.json";
    private const string ActionsFile = "anomaly-actions.json";
    private const string DiagnosticsFile = "diagnostics.json";
    private const int ActiveAnomalyLimit = 256;

    private readonly LaunchOptions _launch;
    private readonly ApplicationStore _store;
    private readonly MonitorEngine _engine;
    private readonly DiskWriteMonitor _monitor;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly System.Windows.Forms.Timer? _smokeTimer;
    private readonly List<Anomaly> _history;
    private readonly List<DiagnosticEntry> _diagnostics;
    private readonly HashSet<Anomaly> _liveAnomalies = [];
    private readonly object _statusLock = new();
    private MonitorStatus _status = new(MonitorState.Starting, "Preparing monitoring.")
    {
        MessageRussian = "Подготовка мониторинга."
    };
    private MonitorStatus? _lastLoggedStatus;
    private (string English, string Russian)? _storageNotice;
    private GuardianOptions _settings;
    private SmokeProbe? _smoke;
    private Anomaly? _latestAnomaly;
    private DetailForm? _openAnomaly;
    private bool _latestIsDemo;
    private bool _exitRequested;
    private bool _disposed;

    internal MainForm(LaunchOptions launch, ApplicationStore store, SettingsProfile profile, Exception? warning)
    {
        _launch = launch;
        _store = store;
        _diagnostics = ReadEntries<DiagnosticEntry>(DiagnosticsFile);
        _history = ReadEntries<Anomaly>(HistoryFile)
            .Where(entry => entry.Process?.Identity is not null && entry.Files is not null && entry.Reason is not null &&
                entry.Files.All(file => file?.Path is not null && file.Logical is not null))
            .ToList();
        _settings = profile.Options;
        if (warning is not null)
        {
            Log($"Using default settings. {warning.Message}", $"Используются настройки по умолчанию. {warning.Message}");
            SetStorageNotice("Could not read settings. Defaults are in use.",
                "Не удалось прочитать настройки. Используются значения по умолчанию.");
        }
        _engine = new MonitorEngine(_settings);
        _engine.RestoreTerminations(ReadEntries<RecentTermination>(ActionsFile, ActiveAnomalyLimit), DateTimeOffset.UtcNow);
        _monitor = new DiskWriteMonitor(_engine, RecordStatus);
        _refreshTimer = new System.Windows.Forms.Timer { Interval = _settings.RefreshSeconds * 1000 };
        _refreshTimer.Tick += (_, _) => RefreshView();
        if (_launch.SmokeDuration is not null)
        {
            _smokeTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _smokeTimer.Tick += (_, _) => SampleSmoke();
        }
    }

    internal int ExitCode { get; private set; }

    internal void Start()
    {
        if (!_launch.Tray)
        {
            BuildLayout();
        }

        _ = Handle;
        BuildTray();
        if (!_launch.Tray)
        {
            Show();
        }
        else
        {
            UpdateHistory();
        }

        if (_smokeTimer is not null)
        {
            _smoke = new SmokeProbe(CurrentStatus(), _store);
            _smokeTimer.Start();
        }

        _monitor.Start();
        RefreshView();
        _refreshTimer.Start();
        if (_launch.Demo)
        {
            BeginInvoke(() => ShowDemo(openDetails: _launch.SmokeDuration is null));
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exitRequested && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _refreshTimer.Dispose();
            _smokeTimer?.Dispose();
            _smoke?.Dispose();
            _monitor.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _trayMenu.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Exit()
    {
        _exitRequested = true;
        Close();
    }

    private void ShowWindow()
    {
        BuildLayout();
        Show();
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        RefreshView();
        Activate();
    }

    private void RecordStatus(MonitorStatus status)
    {
        lock (_statusLock)
        {
            _status = status;
        }
    }

    private MonitorStatus CurrentStatus()
    {
        lock (_statusLock)
        {
            return _status;
        }
    }

    private void RefreshView()
    {
        var now = DateTimeOffset.UtcNow;
        var status = CurrentStatus();
        UpdateStatus(status);
        var anomalies = _engine.Evaluate(now);
        if (anomalies.Count > 0)
        {
            RecordAnomalies(anomalies);
            UpdateStatus(status);
        }

        if (_layoutReady && Visible)
        {
            UpdateSnapshot(_engine.Snapshot(now));
        }
    }

    private void UpdateStatus(MonitorStatus status)
    {
        if (_lastLoggedStatus?.State != status.State ||
            (_lastLoggedStatus?.Message != status.Message && status.State != MonitorState.Degraded))
        {
            Log(status.Message, status.MessageRussian);
            _lastLoggedStatus = status;
        }

        var failed = status.State is MonitorState.Unavailable or MonitorState.Degraded;
        var hasActiveAnomaly = _liveAnomalies.Count > 0;
        _tray.Icon = failed ? SystemIcons.Error : hasActiveAnomaly ? SystemIcons.Warning : SystemIcons.Information;
        _tray.Text = failed ? UiText.Get("SSD Guardian — monitoring status", "SSD Guardian — состояние мониторинга")
            : hasActiveAnomaly ? UiText.Get("SSD Guardian — anomaly detected", "SSD Guardian — обнаружена аномалия") : "SSD Guardian";
        if (_layoutReady)
        {
            _statusLabel.Text = status.GetMessage(UiText.Language);
            _elevationButton.Visible = status.State == MonitorState.Unavailable && !DiskWriteMonitor.IsElevated;
        }
    }

    private void Elevate()
    {
        if (Program.TryElevate(_launch, out var failure))
        {
            Exit();
            return;
        }

        MessageBox.Show(this, failure!, UiText.Get("Run as administrator", "Запуск с правами администратора"),
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SampleSmoke()
    {
        var status = CurrentStatus();
        var smoke = _smoke!;
        if (smoke.Elapsed < _launch.SmokeDuration!.Value)
        {
            smoke.Sample(status);
            return;
        }

        _smokeTimer!.Stop();
        var snapshot = _engine.Snapshot(DateTimeOffset.UtcNow);
        try
        {
            ApplicationStore.WriteTo(_launch.SmokeOutput!, smoke.Finish(snapshot, status), maxBytes: 8 * 1024 * 1024);
        }
        catch (Exception ex) when (ApplicationStore.IsStorageError(ex))
        {
            Log($"Could not save the smoke test results. {ex.Message}", $"Не удалось сохранить результаты проверки. {ex.Message}");
            ExitCode = 1;
        }

        Exit();
    }

    private void ShowSettings()
    {
        var startup = StartupShortcut.IsEnabled;
        using var form = new SettingsForm(_settings, startup, (_, _) => ShowDemo(), (_, _) => ShowDiagnostics());
        if (form.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var profile = new SettingsProfile(form.Options, form.SelectedLanguage);
            profile.Save(_store);
            _settings = form.Options;
            _engine.UpdateOptions(_settings);
            _refreshTimer.Interval = _settings.RefreshSeconds * 1000;
            if (UiText.Language != profile.Language)
            {
                UiText.Language = profile.Language;
                ApplyLanguage();
                UpdateStorageNotice();
                UpdateHistory();
            }

            if (startup != form.StartupEnabled)
            {
                StartupShortcut.SetEnabled(form.StartupEnabled);
            }
        }
        catch (Exception ex) when (ApplicationStore.IsStorageError(ex) || ex is System.Runtime.InteropServices.COMException)
        {
            Log($"Could not save a setting. {ex.Message}", $"Не удалось сохранить настройку. {ex.Message}");
            MessageBox.Show(this, UiText.Get($"Could not save a setting. {ex.Message}", $"Не удалось сохранить настройку. {ex.Message}"),
                UiText.Get("SSD Guardian Settings", "Настройки SSD Guardian"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        RefreshView();
    }

    private List<T> ReadEntries<T>(string file, int limit = ApplicationStore.RetainedEntries) where T : class
    {
        try
        {
            var entries = _store.Read<List<T>>(file);
            return entries?.Where(entry => entry is not null).Take(limit).ToList() ?? [];
        }
        catch (Exception ex) when (ApplicationStore.IsStorageError(ex))
        {
            SetStorageNotice($"Could not read {file}. {ex.Message}", $"Не удалось прочитать {file}. {ex.Message}");
            return [];
        }
    }

    private void WriteEntries<T>(string file, IReadOnlyList<T> entries)
    {
        try
        {
            _store.Write(file, entries);
        }
        catch (Exception ex) when (ApplicationStore.IsStorageError(ex))
        {
            SetStorageNotice($"Could not save {file}. {ex.Message}", $"Не удалось сохранить {file}. {ex.Message}");
        }
    }

    private void SetStorageNotice(string english, string russian)
    {
        _storageNotice = (english, russian);
        UpdateStorageNotice();
    }

    private void UpdateStorageNotice()
    {
        if (_layoutReady)
        {
            _storageLabel.Text = _storageNotice is { } notice ? UiText.Get(notice.English, notice.Russian) : string.Empty;
        }
    }

    private void Log(string english, string? russian = null)
    {
        _diagnostics.Insert(0, new DiagnosticEntry(DateTimeOffset.UtcNow, DisplayText.Shorten(english, 700))
        {
            MessageRussian = russian is null ? null : DisplayText.Shorten(russian, 700)
        });
        if (_diagnostics.Count > ApplicationStore.RetainedEntries)
        {
            _diagnostics.RemoveAt(_diagnostics.Count - 1);
        }

        WriteEntries(DiagnosticsFile, _diagnostics);
    }

    private void ShowDiagnostics()
    {
        var text = string.Join(Environment.NewLine, _diagnostics.Select(entry => $"{DisplayText.Date(entry.Time)}  {entry.GetMessage(UiText.Language)}"));
        using var form = new DetailForm(UiText.Get("SSD Guardian Diagnostics", "Диагностика SSD Guardian"), text);
        form.ShowDialog();
    }
}
