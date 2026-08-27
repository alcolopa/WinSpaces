namespace WindowsSpaces.Core;

/// <summary>Which way the incoming space slides in from.</summary>
public enum TransitionDirection
{
    /// <summary>No directional motion is meaningful (first switch, unknown position).</summary>
    None,

    /// <summary>Moving to a later space: new content enters from the right, old leaves to the left.</summary>
    Next,

    /// <summary>Moving to an earlier space: new content enters from the left, old leaves to the right.</summary>
    Previous
}

/// <summary>
/// One monitor's workspace switch, handed to the animator instead of being
/// applied directly.
///
/// The animator owns <em>when</em> the two window operations happen, because
/// the visual transition is built out of live DWM mirrors of those windows
/// and the ordering matters: the outgoing windows must still be on screen
/// while they slide away, and the incoming ones must already be on screen
/// (behind the animator's overlay) while they slide in.
///
/// It does not own <em>whether</em> they happen. <see cref="ShowIncoming"/>
/// followed by <see cref="HideOutgoing"/> MUST both run exactly once, in that
/// order, whether the animation finishes, is cut short by a later switch on
/// the same monitor, or fails outright. Skipping either one leaves the
/// monitor showing the wrong space.
/// </summary>
public sealed record WorkspaceTransition(
    string MonitorId,
    string? FromWorkspaceId,
    string ToWorkspaceId,
    TransitionDirection Direction,
    IReadOnlyList<nint> OutgoingWindows,
    IReadOnlyList<nint> IncomingWindows,
    Action ShowIncoming,
    Action HideOutgoing);

/// <summary>
/// Renders the visual transition between two spaces. Implemented in the
/// Platform layer; Core knows only this interface, so the switching algorithm
/// stays testable without Win32.
/// </summary>
public interface IWorkspaceTransitionAnimator
{
    /// <summary>
    /// Takes over a workspace switch. Must return promptly — it is called
    /// from the thread driving the switch, which is usually the UI thread —
    /// and must honour the ordering contract described on
    /// <see cref="WorkspaceTransition"/>.
    /// </summary>
    void Animate(WorkspaceTransition transition);
}
