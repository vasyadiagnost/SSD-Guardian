using static SsdGuardian.App.UiText;

namespace SsdGuardian.App;

internal sealed partial class MainForm
{
    private Label _statusLabel = null!;
    private Label _summaryLabel = null!;
    private Label _logicalSummaryLabel = null!;
    private Label _qualityLabel = null!;
    private Label _storageLabel = null!;
    private Button _settingsButton = null!;
    private Button _trayButton = null!;
    private Button _elevationButton = null!;
    private TabControl _tabs = null!;
    private TabPage _processTab = null!;
    private TabPage _sessionTab = null!;
    private TabPage _programTab = null!;
    private TabPage _diskTab = null!;
    private TabPage _historyTab = null!;
    private Label _processHint = null!;
    private Label _sessionHint = null!;
    private Label _programHint = null!;
    private Label _diskHint = null!;
    private Label _historyHint = null!;
    private ListView _processTable = null!;
    private ListView _sessionTable = null!;
    private ListView _programTable = null!;
    private ListView _diskTable = null!;
    private ListView _historyTable = null!;
    private bool _layoutReady;
    private readonly ToolStripMenuItem _openMenuItem = new();
    private readonly ToolStripMenuItem _anomaliesMenuItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _demoMenuItem = new();
    private readonly ToolStripMenuItem _exitMenuItem = new();
    private readonly NotifyIcon _tray = new() { Text = "SSD Guardian", Icon = SystemIcons.Application };
    private readonly ContextMenuStrip _trayMenu = new();

    private void BuildLayout()
    {
        if (_layoutReady)
        {
            return;
        }

        _statusLabel = new() { AutoSize = true, Name = "MonitoringStatus" };
        _summaryLabel = new() { AutoSize = true, Name = "SessionSummary" };
        _logicalSummaryLabel = new() { AutoSize = true, Name = "LogicalSessionSummary" };
        _qualityLabel = new() { AutoSize = true, Name = "DataQuality" };
        _storageLabel = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
        _settingsButton = new() { AutoSize = true, Name = "SettingsButton" };
        _trayButton = new() { AutoSize = true };
        _elevationButton = new() { AutoSize = true, Visible = false };
        _tabs = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 4) };
        _processTab = new() { Name = "ProcessesTab" };
        _sessionTab = new() { Name = "SessionTotalsTab" };
        _programTab = new() { Name = "ProgramsTab" };
        _diskTab = new() { Name = "DisksTab" };
        _historyTab = new() { Name = "AnomaliesTab" };
        _processHint = CreateHint();
        _sessionHint = CreateHint();
        _programHint = CreateHint();
        _diskHint = CreateHint();
        _historyHint = CreateHint();
        _processTable = CreateTable("Processes");
        _sessionTable = CreateTable("SessionTotals");
        _programTable = CreateTable("Programs");
        _processTable.VirtualMode = true;
        _programTable.VirtualMode = true;
        _processTable.RetrieveVirtualItem += (_, e) => e.Item = MakeProcessRow(_processRows[e.ItemIndex]);
        _programTable.RetrieveVirtualItem += (_, e) => e.Item = MakeProgramRow(_programRows[e.ItemIndex]);
        _diskTable = CreateTable("SsdDisks");
        _historyTable = CreateTable("Anomalies");

        Text = $"SSD Guardian v{Application.ProductVersion.Split('+', 2)[0]}";
        Icon = SystemIcons.Application;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(990, 560);
        MinimumSize = new Size(710, 405);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_statusLabel, 0, 0);
        layout.Controls.Add(_summaryLabel, 0, 1);
        layout.Controls.Add(_logicalSummaryLabel, 0, 2);
        layout.Controls.Add(_qualityLabel, 0, 3);
        FitLabelWidth(layout, _qualityLabel);

        PopulateTab(_processTab, _processHint, _processTable);
        PopulateTab(_sessionTab, _sessionHint, _sessionTable);
        PopulateTab(_programTab, _programHint, _programTable);
        PopulateTab(_diskTab, _diskHint, _diskTable);
        PopulateTab(_historyTab, _historyHint, _historyTable);
        _tabs.TabPages.AddRange([_processTab, _sessionTab, _programTab, _diskTab, _historyTab]);
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_latestSnapshot is { } snapshot)
            {
                UpdateSelectedSnapshotTable(snapshot);
            }
        };
        layout.Controls.Add(_tabs, 0, 4);
        layout.Controls.Add(_storageLabel, 0, 5);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 8, 0, 0)
        };
        _settingsButton.Click += (_, _) => ShowSettings();
        _trayButton.Click += (_, _) => Hide();
        _elevationButton.Click += (_, _) => Elevate();
        buttons.Controls.AddRange([_settingsButton, _trayButton, _elevationButton]);
        layout.Controls.Add(buttons, 0, 6);
        Controls.Add(layout);

        _processTable.Columns.Add("", 144);
        _processTable.Columns.Add("PID", 60, HorizontalAlignment.Right);
        _processTable.Columns.Add("SSD", 86, HorizontalAlignment.Right);
        _processTable.Columns.Add("", 126);
        _processTable.Columns.Add("", 82, HorizontalAlignment.Right);
        _processTable.Columns.Add("", 82, HorizontalAlignment.Right);
        _processTable.Columns.Add("", 82, HorizontalAlignment.Right);
        _processTable.Columns.Add("", 106, HorizontalAlignment.Right);
        _processTable.Columns.Add("", 90, HorizontalAlignment.Right);
        _processTable.Columns.Add("", 180);
        _processTable.HeaderStyle = ColumnHeaderStyle.Clickable;
        _processTable.ColumnClick += (_, e) => ChangeProcessSort(e.Column);
        _processTable.DoubleClick += (_, _) => OpenSelectedProcess();
        _processTable.KeyDown += (_, e) => OpenOnEnter(e, OpenSelectedProcess);

        _sessionTable.Columns.Add("", 374);
        _sessionTable.Columns.Add("", 155, HorizontalAlignment.Right);
        _sessionTable.Columns.Add("", 144, HorizontalAlignment.Right);

        _programTable.Columns.Add("", 241);
        _programTable.Columns.Add("", 104, HorizontalAlignment.Right);
        _programTable.Columns.Add("", 90, HorizontalAlignment.Right);
        _programTable.Columns.Add("", 72, HorizontalAlignment.Right);
        _programTable.Columns.Add("", 110, HorizontalAlignment.Right);
        _programTable.Columns.Add("", 365);
        _programTable.HeaderStyle = ColumnHeaderStyle.Clickable;
        _programTable.ColumnClick += (_, e) => ChangeProgramSort(e.Column);
        _programTable.ShowItemToolTips = true;
        _programTable.DoubleClick += (_, _) => OpenSelectedProgram();
        _programTable.KeyDown += (_, e) => OpenOnEnter(e, OpenSelectedProgram);

        _diskTable.Columns.Add("", 58);
        _diskTable.Columns.Add("", 200);
        _diskTable.Columns.Add("", 148);
        _diskTable.Columns.Add("", 98, HorizontalAlignment.Right);
        _diskTable.Columns.Add("", 98, HorizontalAlignment.Right);
        _diskTable.Columns.Add("", 98, HorizontalAlignment.Right);
        _diskTable.Columns.Add("", 104, HorizontalAlignment.Right);
        _diskTable.Columns.Add("", 93, HorizontalAlignment.Right);
        _diskTable.DoubleClick += (_, _) => OpenSelectedDisk();
        _diskTable.KeyDown += (_, e) => OpenOnEnter(e, OpenSelectedDisk);

        _historyTable.Columns.Add("", 133);
        _historyTable.Columns.Add("", 156);
        _historyTable.Columns.Add("SSD", 48, HorizontalAlignment.Right);
        _historyTable.Columns.Add("", 140, HorizontalAlignment.Right);
        _historyTable.Columns.Add("", 80, HorizontalAlignment.Right);
        _historyTable.Columns.Add("", 85);
        _historyTable.Columns.Add("", 360);
        _historyTable.DoubleClick += (_, _) => OpenSelectedAnomaly();
        _historyTable.KeyDown += (_, e) => OpenOnEnter(e, OpenSelectedAnomaly);
        _layoutReady = true;
        ApplyLanguage();
        UpdateStorageNotice();
        UpdateHistory();
    }

    private void ApplyLanguage()
    {
        ApplyTrayLanguage();
        if (!_layoutReady)
        {
            return;
        }

        _processTab.Text = Get("Processes", "Процессы");
        _sessionTab.Text = Get("Session totals", "Итоги сеанса");
        _programTab.Text = Get("Executables", "Программы");
        _diskTab.Text = Get("Drives", "Накопители");
        _historyTab.Text = Get("Anomalies", "Аномалии");
        _processHint.Text = Get("Logical writes by process instance and SSD. Double-click for details. ≥ marks partial totals and — marks unavailable detail.",
            "Логическая запись по экземплярам процессов и SSD. Двойной щелчок открывает подробности. ≥ — неполный учёт, прочерк — нет данных.");
        _sessionHint.Text = Get("The four indented rows add up to logical writes on confirmed SSDs. Physical writes and writes on undetermined drives are shown separately.",
            "Четыре строки с отступом в сумме дают логическую запись на подтверждённых SSD. Физическая запись и запись на неопределённых дисках показаны отдельно.");
        _programHint.Text = Get("Logical writes by verified executable path. Runs count distinct instances that wrote to SSD. ≥ marks partial totals. Double-click for details.",
            "Логическая запись по подтверждённому пути программы. Запуски — отдельные экземпляры, писавшие на SSD. ≥ — неполный учёт. Двойной щелчок открывает подробности.");
        _diskHint.Text = Get("Physical writes per SSD and the volumes on each drive.",
            "Физическая запись по SSD и тома каждого накопителя.");
        _historyHint.Text = Get("Active anomalies and the latest 100 records. Double-click for details and available actions.",
            "Активные аномалии и последние 100 записей. Дважды щёлкните строку для подробностей и действий.");
        _settingsButton.Text = Get("Settings", "Настройки");
        _trayButton.Text = Get("Minimize to tray", "Свернуть в трей");
        _elevationButton.Text = Get("Run as administrator", "Запустить с правами администратора");

        SetProcessColumnText();
        SetProgramColumnText();
        var process = Get("Process", "Процесс");
        var min10 = Get("10 min", "10 мин");
        var min30 = Get("30 min", "30 мин");
        var min60 = Get("60 min", "60 мин");
        var session = Get("Session", "За сеанс");
        var operations = Get("Operations", "Операций");
        SetColumnText(_sessionTable, Get("Recorded writes", "Учтённая запись"), Get("Bytes", "Байт"), operations);
        SetColumnText(_diskTable, Get("Disk", "Диск"), Get("Model", "Модель"), Get("Volumes", "Тома"),
            min10, min30, min60, session, operations);
        SetColumnText(_historyTable, Get("Detected", "Обнаружена"), process, "SSD",
            Get("Logical bytes", "Логический объём"), Get("Window", "Интервал"), Get("Status", "Статус"), Get("Reason", "Причина"));
    }

    private void ApplyTrayLanguage()
    {
        _openMenuItem.Text = Get("Open SSD Guardian", "Открыть SSD Guardian");
        _anomaliesMenuItem.Text = Get("View anomalies", "Просмотреть аномалии");
        _demoMenuItem.Text = Get("Demo notification", "Демо уведомления");
        _exitMenuItem.Text = Get("Exit", "Выход");
    }

    private static void SetColumnText(ListView table, params string[] captions)
    {
        for (var index = 0; index < captions.Length; index++)
        {
            table.Columns[index].Text = captions[index];
        }
    }

    private static Label CreateHint() => new() { AutoSize = true, Margin = new Padding(0, 2, 0, 6) };

    private static void PopulateTab(TabPage page, Label hint, ListView table)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(4) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(hint, 0, 0);
        layout.Controls.Add(table, 0, 1);
        FitLabelWidth(layout, hint);
        page.Controls.Add(layout);
    }

    private static void FitLabelWidth(Control parent, Label label)
    {
        void SetWidth() => label.MaximumSize = new Size(Math.Max(1, parent.ClientSize.Width - parent.Padding.Horizontal - label.Margin.Horizontal), 0);
        parent.ClientSizeChanged += (_, _) => SetWidth();
        SetWidth();
    }

    private static ListView CreateTable(string name) => new()
    {
        Name = name,
        Dock = DockStyle.Fill,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        View = View.Details,
        HeaderStyle = ColumnHeaderStyle.Nonclickable
    };

    private static void OpenOnEnter(KeyEventArgs args, Action open)
    {
        if (args.KeyCode == Keys.Enter)
        {
            args.SuppressKeyPress = true;
            open();
        }
    }

    private void BuildTray()
    {
        ApplyTrayLanguage();
        _openMenuItem.Click += (_, _) => ShowWindow();
        _anomaliesMenuItem.Click += (_, _) => ShowAnomalyList();
        _demoMenuItem.Click += (_, _) => ShowDemo();
        _exitMenuItem.Click += (_, _) => Exit();
        _trayMenu.Items.AddRange([_openMenuItem, _anomaliesMenuItem, _demoMenuItem, new ToolStripSeparator(), _exitMenuItem]);
        _tray.ContextMenuStrip = _trayMenu;
        _tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
        _tray.BalloonTipClicked += (_, _) => OpenNotification();
        _tray.Visible = true;
    }
}
