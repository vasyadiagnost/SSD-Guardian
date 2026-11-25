namespace SsdGuardian.App;

internal sealed class DetailForm : Form
{
    private readonly TextBox _details;

    internal DetailForm(string title, string details, params (string Caption, EventHandler Click)[] actions)
    {
        Text = title;
        ClientSize = new Size(690, 460);
        MinimumSize = new Size(440, 310);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _details = new TextBox
        {
            Name = "Details",
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = details,
            TabStop = false
        };
        layout.Controls.Add(_details, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 8, 0, 0)
        };
        var close = new Button { Text = UiText.Get("Close", "Закрыть"), AutoSize = true, DialogResult = DialogResult.Cancel };
        close.Click += (_, _) => Close();
        buttons.Controls.Add(close);
        CancelButton = close;

        foreach (var action in actions)
        {
            var button = new Button { Text = action.Caption, AutoSize = true };
            button.Click += action.Click;
            buttons.Controls.Add(button);
        }

        layout.Controls.Add(buttons, 0, 1);
        Controls.Add(layout);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _details.Select(0, 0);
    }
}
