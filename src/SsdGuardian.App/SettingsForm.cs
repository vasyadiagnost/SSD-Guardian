using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using SsdGuardian.Core;

namespace SsdGuardian.App;

internal sealed class SettingsForm : Form
{
    private const decimal BytesPerGb = 1_000_000_000m;
    private static readonly (string Property, string English, string Russian)[] ConfigurableRules =
    [
        (nameof(GuardianOptions.LongThresholdBytes), "Sustained write threshold, GB", "Порог длительной записи, ГБ"),
        (nameof(GuardianOptions.FastThresholdBytes), "High-rate write threshold, GB", "Порог интенсивной записи, ГБ"),
        (nameof(GuardianOptions.RefreshSeconds), "Statistics refresh interval, s", "Интервал обновления статистики, с"),
        (nameof(GuardianOptions.LongWindowMinutes), "Sustained write window, min", "Окно длительной записи, мин"),
        (nameof(GuardianOptions.FastWindowMinutes), "High-rate write window, min", "Окно интенсивной записи, мин"),
        (nameof(GuardianOptions.ActivityIntervalMinutes), "Write activity interval length, min", "Длительность интервала активности записи, мин"),
        (nameof(GuardianOptions.RequiredActiveFiveMinuteIntervals), "Required active intervals (sustained)", "Активных интервалов при длительной записи"),
        (nameof(GuardianOptions.FastRequiredActiveFiveMinuteIntervals), "Required active intervals (high rate)", "Активных интервалов при интенсивной записи"),
        (nameof(GuardianOptions.LongMinimumActivitySeconds), "Minimum sustained write duration, s", "Минимальная длительность фоновой записи, с"),
        (nameof(GuardianOptions.FastMinimumActivitySeconds), "Minimum high-rate write duration, s", "Минимальная длительность интенсивной записи, с"),
        (nameof(GuardianOptions.MinimumActiveIntervalBytes), "Minimum logical bytes written per interval", "Минимум логической записи за интервал, Б"),
        (nameof(GuardianOptions.MinimumPhysicalIntervalBytes), "Minimum physical bytes written per interval", "Минимум физической записи за интервал, Б"),
        (nameof(GuardianOptions.MinimumCorrelatedIntervals), "Required intervals with physical writes", "Интервалов с совпадением физической записи"),
        (nameof(GuardianOptions.MinimumPhysicalToLogicalRatio), "Minimum ratio of physical to logical writes", "Минимальная доля физической записи к логической"),
        (nameof(GuardianOptions.MinimumDominantFileShare), "Minimum share of writes to repeatedly overwritten files", "Минимальная доля записи в перезаписываемые файлы"),
        (nameof(GuardianOptions.MinimumRepeatedRangeShare), "Minimum share of repeated file ranges", "Минимальная доля повторяющихся диапазонов"),
        (nameof(GuardianOptions.RecentRangeCount), "Maximum recent ranges tracked per file", "Лимит недавних диапазонов записи на файл"),
        (nameof(GuardianOptions.RecentRangeAgeSeconds), "How long to remember recent write ranges, s", "Срок хранения недавних диапазонов записи, с"),
        (nameof(GuardianOptions.SmallFileMaximumBytes), "Maximum size of a small file, bytes", "Максимальный размер малого файла, Б"),
        (nameof(GuardianOptions.MinimumOverwriteMultiple), "Minimum logical writes as a multiple of file size", "Минимальная логическая запись к размеру файла, раз"),
        (nameof(GuardianOptions.MaximumFileGrowthShare), "Maximum relative file growth", "Максимальный относительный прирост файла"),
        (nameof(GuardianOptions.MinimumSizeObservationSeconds), "Minimum file size observation time, s", "Минимальное время наблюдения за размером файла, с"),
        (nameof(GuardianOptions.FileSizeSampleIntervalSeconds), "File size sampling interval, s", "Интервал проверки размера файла, с"),
        (nameof(GuardianOptions.NotificationCooldownMinutes), "Pause between notifications, min", "Пауза между уведомлениями, мин"),
        (nameof(GuardianOptions.EnableNvidiaSignature), "Enable NVIDIA diagnostic signature", "Включить диагностическую сигнатуру NVIDIA"),
        (nameof(GuardianOptions.NvidiaProcessName), "NVIDIA signature process name", "Имя процесса сигнатуры NVIDIA"),
        (nameof(GuardianOptions.NvidiaFileName), "NVIDIA signature file name", "Имя файла сигнатуры NVIDIA"),
        (nameof(GuardianOptions.NvidiaDiagnosticThresholdBytes), "Minimum logical bytes for NVIDIA diagnostics", "Минимум логической записи для диагностики NVIDIA, Б"),
        (nameof(GuardianOptions.RecurrenceRetentionDays), "Time to remember confirmed process termination, days", "Срок учёта подтверждённого завершения процесса, дн."),
        (nameof(GuardianOptions.MaxTrackedProcesses), "Maximum tracked processes", "Лимит отслеживаемых процессов"),
        (nameof(GuardianOptions.MaxFilesPerProcess), "Maximum tracked files per process", "Лимит отслеживаемых файлов на процесс"),
        (nameof(GuardianOptions.TrackingIdleSeconds), "Idle time before replacing a tracked entry, s", "Время простоя до замены записи детализации, с"),
        (nameof(GuardianOptions.TrackingPromotionRatio), "Write volume ratio to replace an active tracked entry", "Отношение объёма записи для замены активной записи детализации")
    ];

    private readonly ComboBox _language;
    private readonly CheckBox _startup;
    private readonly DataGridView _grid;

    internal SettingsForm(GuardianOptions options, bool startup, EventHandler showDemo, EventHandler showDiagnostics)
    {
        Options = options;
        Text = UiText.Get("SSD Guardian Settings", "Настройки SSD Guardian");
        ClientSize = new Size(620, 620);
        MinimumSize = new Size(490, 390);
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var languages = new[]
        {
            new LanguageChoice(UiLanguage.English, "English"),
            new LanguageChoice(UiLanguage.Russian, "Русский")
        };
        _language = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            AccessibleName = UiText.Get("Interface language", "Язык интерфейса"),
            DisplayMember = nameof(LanguageChoice.Label)
        };
        _language.Items.AddRange(languages);
        _language.SelectedIndex = UiText.Language == UiLanguage.Russian ? 1 : 0;
        var languageRow = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 8)
        };
        languageRow.Controls.Add(new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 6, 5, 0),
            Text = UiText.Get("&Language", "&Язык")
        });
        languageRow.Controls.Add(_language);
        layout.Controls.Add(languageRow, 0, 0);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = UiText.Get("1 GB = 1,000,000,000 bytes. Enter shares from 0 to 1 and ratios as multiples.",
                "1 ГБ = 1 000 000 000 Б. Доли задаются от 0 до 1, отношения — в разах.")
        }, 0, 1);

        _grid = CreateRuleGrid(options);
        layout.Controls.Add(_grid, 0, 2);

        _startup = new CheckBox
        {
            AutoSize = true,
            Checked = startup,
            Text = UiText.Get("Start when I sign in to Windows. UAC may prompt for permission.",
                "Запускать при входе в Windows. При необходимости появится запрос UAC."),
            Margin = new Padding(0, 8, 0, 0)
        };
        layout.Controls.Add(_startup, 0, 3);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 8, 0, 0)
        };
        var cancel = new Button { Text = UiText.Get("Cancel", "Отмена"), AutoSize = true, DialogResult = DialogResult.Cancel };
        var save = new Button { Text = UiText.Get("Save", "Сохранить"), AutoSize = true };
        var demo = new Button { Text = UiText.Get("Preview notification", "Демо уведомления"), AutoSize = true };
        var diagnostics = new Button { Text = UiText.Get("Diagnostics", "Диагностика"), AutoSize = true };
        save.Click += (_, _) => ValidateAndClose();
        demo.Click += showDemo;
        diagnostics.Click += showDiagnostics;
        buttons.Controls.AddRange([cancel, save, demo, diagnostics]);
        layout.Controls.Add(buttons, 0, 4);
        AcceptButton = save;
        CancelButton = cancel;
        Controls.Add(layout);
    }

    internal GuardianOptions Options { get; private set; }
    internal bool StartupEnabled => _startup.Checked;
    internal UiLanguage SelectedLanguage => _language.SelectedItem is LanguageChoice choice ? choice.Language : UiText.Language;

    private static DataGridView CreateRuleGrid(GuardianOptions options)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            EditMode = DataGridViewEditMode.EditOnEnter
        };
        grid.DefaultCellStyle.FormatProvider = UiText.Culture;
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = UiText.Get("Setting", "Параметр"), ReadOnly = true, FillWeight = 70, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = UiText.Get("Value", "Значение"), FillWeight = 30, SortMode = DataGridViewColumnSortMode.NotSortable });

        foreach (var rule in ConfigurableRules)
        {
            var property = typeof(GuardianOptions).GetProperty(rule.Property)!;
            var value = property.GetValue(options) ?? "";
            var row = grid.Rows[grid.Rows.Add(UiText.Get(rule.English, rule.Russian), IsGbThreshold(property) ? (long)value / BytesPerGb : value)];
            row.Tag = property;
            if (property.PropertyType == typeof(bool))
            {
                row.Cells[1] = new DataGridViewCheckBoxCell { Value = value };
            }
        }

        return grid;
    }

    private static bool IsGbThreshold(PropertyInfo property) => property.Name is
        nameof(GuardianOptions.LongThresholdBytes) or nameof(GuardianOptions.FastThresholdBytes);

    private void ValidateAndClose()
    {
        if (!_grid.EndEdit())
        {
            return;
        }

        var options = Options with { };
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var property = (PropertyInfo)row.Tag!;
            try
            {
                var value = ReadValue(property, row.Cells[1].Value);
                property.SetValue(options, value);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException or NotSupportedException or OverflowException)
            {
                _grid.CurrentCell = row.Cells[1];
                var label = row.Cells[0].Value;
                ShowValidationError(UiText.Get($"Check the value for “{label}”.", $"Проверьте значение параметра «{label}»."));
                return;
            }
        }

        try
        {
            options.Validate(UiText.Language);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            var rule = ConfigurableRules.FirstOrDefault(rule => rule.Property == ex.ParamName);
            var label = rule.Property is null ? ex.ParamName : UiText.Get(rule.English, rule.Russian);
            ShowValidationError(UiText.Get($"“{label}” is outside the allowed range.",
                $"Значение параметра «{label}» вне допустимого диапазона."));
            return;
        }

        Options = options;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ShowValidationError(string message) =>
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private static object? ReadValue(PropertyInfo property, object? value)
    {
        var culture = UiText.Culture;
        var text = Convert.ToString(value, culture) ?? "";
        if (IsGbThreshold(property))
        {
            var bytes = decimal.Parse(text, NumberStyles.Number, culture) * BytesPerGb;
            if (bytes != decimal.Truncate(bytes))
            {
                throw new FormatException();
            }

            return decimal.ToInt64(bytes);
        }

        return property.PropertyType.IsInstanceOfType(value)
            ? value
            : TypeDescriptor.GetConverter(property.PropertyType).ConvertFromString(null, culture, text);
    }

    private sealed record LanguageChoice(UiLanguage Language, string Label);
}
