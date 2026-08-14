using WindowsSpaces.Core;

namespace WindowsSpaces.Tests.Fakes;

public sealed class FakeWindowEventSource : IWindowEventSource
{
    public event EventHandler<WindowEvent>? WindowEvent;
    public bool Started { get; private set; }

    public void Start() => Started = true;
    public void Stop() => Started = false;

    public void Raise(WindowEvent evt) => WindowEvent?.Invoke(this, evt);
}
