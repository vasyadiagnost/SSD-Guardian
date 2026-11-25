using static SsdGuardian.App.UiText;

namespace SsdGuardian.App;

internal sealed partial class MainForm
{
    private Label _statusLabel = null!;
    private Label _summaryLabel = null!;
    private Label _qualityLabel = null!;
    private Label _storageLabel = null!;
    private Button _settingsButton = null!;
    private Button _trayButton = null!;
    private Button _elevationButton = null!;
    private TabControl _tabs = null!;
    private TabPage _processTab = null!;
    private TabPage _diskTab = null!;
    private TabPage _historyTab = null!;
    private Label _processHint = null!;
    private Label _diskHint = null!;
    private Label _historyHint = null!;
    private ListView _processTable = null!;
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
        _qualityLabel = new() { AutoSize = true, Name = "DataQuality" };
        _storageLabel = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
        _settingsButton = new() { AutoSize = true, Name = "SettingsButton" };
        _trayButton = new() { AutoSize = true };
        _elevationButton = new() { AutoSize = true, Visible = false };
        _tabs = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 4) };
        _processTab = new() { Name = "ProcessesTab" };
        _diskTab = new() { Name = "DisksTab" };
        _historyTab = new() { Name = "AnomaliesTab" };
        _processHint = CreateHint();
        _diskHint = CreateHint();
        _historyHint = CreateHint();
        _processTable = CreateTable("Processes");
        _diskTable = CreateTable("SsdDisks");
        _historyTable = CreateTable("Anomalies");

        Text = $"SSD Guardian v{Application.ProductVersion.Split('+', 2)[0]}";
        Icon = SystemIcons.Application;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(990, 560);
        MinimumSize = new Size(710, 405);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_statusLabel, 0, 0);
        layout.Controls.Add(_summaryLabel, 0, 1);
        layout.Controls.Add(_qualityLabel, 0, 2);

        PopulateTab(_processTab, _processHint, _processTable);
        PopulateTab(_diskTab, _diskHint, _diskTable);
        PopulateTab(_historyTab, _historyHint, _historyTable);
        _tabs.TabPages.AddRange([_processTab, _diskTab, _historyTab]);
        layout.Controls.Add(_tabs, 0, 3);
        layout.Controls.Add(_storageLabel, 0, 4);

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
        layout.Controls.Add(buttons, 0, 5);
        Controls.Add(layout);

        _processTable.Columns.Add("", 155);
        _processTable.Columns.Add("PID", 62, HorizontalAlignment.Right);
        _processTable.Columns.Add("SSD", 46, HorizontalAlignment.Right);
        _processTable.Columns.Add("", 98, HorizontalAlignment.Right);
        _processTable.Columns.Add("", 98, HorizontalAlignment.Right);
        _processTable.Columns.Add("", 98, HorizontalAlignment.Right);
        _processTable.Columns.Add("", 104, HorizontalAlignment.Right);
        _processTable.Columns.Add("", 93, HorizontalAlignment.Right);
        _processTable.Columns.Add("", 180);
        _processTable.HeaderStyle = ColumnHeaderStyle.Clickable;
        _processTable.ColumnClick += (_, e) => ChangeProcessSort(e.Column);
        _processTable.DoubleClick += (_, _) => OpenSelectedProcess();
        _processTable.KeyDown += (_, e) => OpenOnEnter(e, OpenSelectedProcess);

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
        _processTab.Text = Get("Processes", "Процессы");
        _diskTab.Text = Get("Drives", "Накопители");
        _historyTab.Text = Get("Anomalies", "Аномалии");
        _processHint.Text = Get("Logical file writes on SSDs. Double-click a row for details.",
            "Логическая запись в файлы на SSD. Дважды щёлкните строку для подробностей.");
        _diskHint.Text = Get("Physical writes per SSD and the volumes on each drive.",
            "Физическая запись по SSD и тома каждого накопителя.");
        _historyHint.Text = Get("Active anomalies and the latest 100 records. Double-click for details and available actions.",
            "Активные аномалии и последние 100 записей. Дважды щёлкните строку для подробностей и действий.");
        _settingsButton.Text = Get("Settings", "Настройки");
        _trayButton.Text = Get("Minimize to tray", "Свернуть в трей");
        _elevationButton.Text = Get("Run as administrator", "Запустить с правами администратора");

        SetProcessColumnText();
        var process = Get("Process", "Процесс");
        var min10 = Get("10 min", "10 мин");
        var min30 = Get("30 min", "30 мин");
        var min60 = Get("60 min", "60 мин");
        var session = Get("Session", "За сеанс");
        var operations = Get("Operations", "Операций");
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
        page.Controls.Add(layout);
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
