using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Threading;

class Program
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    static void Main()
    {
        Console.WriteLine("Creating a form...");
        Form form = null;
        Thread thread = new Thread(() =>
        {
            form = new Form();
            form.Text = "MyTestWindow";
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // Wait for form to be created and shown
        Thread.Sleep(2000);

        Console.WriteLine("Starting EnumWindows test...");
        int count = 0;
        bool success = EnumWindows((hWnd, lParam) => {
            count++;
            Console.WriteLine($"Callback called with HWND: {hWnd}");
            return true;
        }, 0);
        Console.WriteLine($"EnumWindows returned: {success}, Count: {count}, LastError: {Marshal.GetLastWin32Error()}");

        if (form != null)
        {
            form.Invoke(new Action(() => form.Close()));
        }
        thread.Join();
    }
}
