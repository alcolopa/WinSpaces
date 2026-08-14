namespace WindowsSpaces.TestApp;

public sealed class TestWindowForm : Form
{
    public TestWindowForm(string title, Size size, Point location, FormWindowState startState = FormWindowState.Normal, bool alwaysOnTop = false)
    {
        Text = title;
        Size = size;
        StartPosition = FormStartPosition.Manual;
        Location = location;
        WindowState = startState;
        TopMost = alwaysOnTop;

        var label = new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.FontFamily, 16)
        };
        Controls.Add(label);
    }
}
