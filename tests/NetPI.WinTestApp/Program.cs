// netpi-wintest: the window the windows tool's tests drive. A title of its own (argument 1, so parallel runs never
// share a window), a name box, an Add button that lists the name, a check box, a drop-down, a slider and a status line.
using System.Windows.Forms;

ApplicationConfiguration.Initialize();
var title = args.Length > 0 ? args[0] : "NetPI test window";
var form = new Form { Text = title, Width = 460, Height = 420, StartPosition = FormStartPosition.Manual, Left = 40, Top = 40 };
var name = new TextBox { Name = "NameBox", AccessibleName = "Name", Left = 12, Top = 12, Width = 200 };
var add = new Button { Text = "Add", Left = 220, Top = 10, Width = 80 };
var list = new ListBox { Name = "People", AccessibleName = "People", Left = 12, Top = 44, Width = 288, Height = 120 };
var agree = new CheckBox { Text = "I agree", Left = 12, Top = 172, Width = 120 };
var color = new ComboBox { Name = "Color", AccessibleName = "Color", DropDownStyle = ComboBoxStyle.DropDownList, Left = 140, Top = 172, Width = 160 };
color.Items.AddRange(["Red", "Green", "Blue"]);
var slider = new TrackBar { AccessibleName = "Volume", Minimum = 0, Maximum = 10, Left = 12, Top = 204, Width = 288 };
var status = new Label { Name = "Status", Text = "Ready", Left = 12, Top = 260, Width = 420 };
var dialog = new Button { Text = "Ask", Left = 310, Top = 10, Width = 80 };
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
dialog.Click += (_, _) => status.Text = MessageBox.Show(form, "Proceed?", "Question", MessageBoxButtons.YesNo) == DialogResult.Yes ? "Proceeding" : "Stopped";
form.Controls.AddRange([name, add, list, agree, color, slider, status, dialog]);
Application.Run(form);
