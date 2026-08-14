namespace WindowsSpaces.Core;

public interface IWindowEventSource
{
    event EventHandler<WindowEvent>? WindowEvent;
    void Start();
    void Stop();
}
