namespace WindowsSpaces.TestApp;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var windows = new List<Form>
        {
            new TestWindowForm("SpacesTest-Normal-1", new Size(500, 400), new Point(100, 100)),
            new TestWindowForm("SpacesTest-Normal-2", new Size(500, 400), new Point(650, 100)),
            new TestWindowForm("SpacesTest-Maximized-1", new Size(500, 400), new Point(100, 550), FormWindowState.Maximized),
            new TestWindowForm("SpacesTest-Minimized-1", new Size(500, 400), new Point(650, 550), FormWindowState.Minimized),
            new TestWindowForm("SpacesTest-AlwaysOnTop-1", new Size(300, 200), new Point(1200, 100), alwaysOnTop: true),
        };

        foreach (var window in windows)
        {
            window.Show();
        }

        Application.Run();
    }
}
