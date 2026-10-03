// netpi-wintest: the window the windows tool's tests drive. A title of its own (argument 1, so parallel runs never
// share a window), a name box, an Add button that lists the name, a check box, a drop-down, a slider, a status line and
// an Ask button that opens an owned "Question" window. The tests run on the owner's desktop, so the windows are
// see-through, never take the focus and have no taskbar button: UI Automation reads them all the same.
using System.Windows.Forms;

ApplicationConfiguration.Initialize();
var title = args.Length > 0 ? args[0] : "NetPI test window";
var form = new QuietForm { Text = title, Width = 460, Height = 420, StartPosition = FormStartPosition.Manual, Left = 40, Top = 40 };
var name = new TextBox { Name = "NameBox", AccessibleName = "Name", Left = 12, Top = 12, Width = 200 };
var add = new Button { Text = "Add", Left = 220, Top = 10, Width = 80 };
var list = new ListBox { Name = "People", AccessibleName = "People", Left = 12, Top = 44, Width = 288, Height = 120 };
var agree = new CheckBox { Text = "I agree", Left = 12, Top = 172, Width = 120 };
var color = new ComboBox { Name = "Color", AccessibleName = "Color", DropDownStyle = ComboBoxStyle.DropDownList, Left = 140, Top = 172, Width = 160 };
color.Items.AddRange(["Red", "Green", "Blue"]);
var slider = new TrackBar { AccessibleName = "Volume", Minimum = 0, Maximum = 10, Left = 12, Top = 204, Width = 288 };
var status = new Label { Name = "Status", Text = "Ready", Left = 12, Top = 260, Width = 420 };
var ask = new Button { Text = "Ask", Left = 310, Top = 10, Width = 80 };
add.Click += (_, _) =>
{
    if (name.Text.Length == 0) return;
    list.Items.Add(name.Text);
    status.Text = $"Added {name.Text}";
    name.Text = "";
};
agree.CheckedChanged += (_, _) => status.Text = agree.Checked ? "Agreed" : "Not agreed";
color.SelectedIndexChanged += (_, _) => status.Text = $"Color {color.SelectedItem}";
slider.ValueChanged += (_, _) => status.Text = $"Volume {slider.Value}";
ask.Click += (_, _) =>
{
    // an owned dialog-like window (a real message box would show itself and take the focus)
    var question = new QuietForm { Text = "Question", Width = 260, Height = 140, StartPosition = FormStartPosition.Manual, Left = 120, Top = 140 };
    var text = new Label { Text = "Proceed?", Left = 12, Top = 12, Width = 200 };
    var yes = new Button { Text = "Yes", Left = 12, Top = 50, Width = 80 };
    var no = new Button { Text = "No", Left = 110, Top = 50, Width = 80 };
    yes.Click += (_, _) => { status.Text = "Proceeding"; question.Close(); };
    no.Click += (_, _) => { status.Text = "Stopped"; question.Close(); };
    question.Controls.AddRange([text, yes, no]);
    question.Show(form);
};
form.Controls.AddRange([name, add, list, agree, color, slider, status, ask]);
Application.Run(form);

/// <summary>A window the user does not see or get interrupted by: almost transparent, never activated, no taskbar button.</summary>
internal sealed class QuietForm : Form
{
    public QuietForm()
    {
        Opacity = 0.01;
        ShowInTaskbar = false;
    }

    protected override bool ShowWithoutActivation => true;
}
