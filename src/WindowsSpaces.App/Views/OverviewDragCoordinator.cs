using System;
using System.Collections.Generic;
using System.Linq;

namespace WindowsSpaces.App.Views;

/// <summary>What is being dragged in the overview.</summary>
public enum OverviewDragKind
{
    /// <summary>A single window tile.</summary>
    Window,

    /// <summary>A whole space card, with every window on it.</summary>
    Workspace
}

/// <summary>One in-flight drag, shared by every overview pane.</summary>
public sealed record OverviewDragSession(
    OverviewDragKind Kind,
    OverviewWindow SourcePane,
    string SourceMonitorId,
    nint Hwnd,
    string WorkspaceId,
    string GhostText);

/// <summary>
/// Ties the per-monitor overview panes together for the duration of a drag.
///
/// Each pane is its own top-level window covering one monitor, so a drag that
/// crosses a monitor edge starts in one pane and ends in another: the pane the
/// pointer was pressed in keeps the pointer capture and reports the cursor in
/// *screen* coordinates here, and this broadcasts it to every pane so the one
/// under the pointer can draw its own drop feedback and, on release, be
/// resolved as the drop target.
///
/// Screen coordinates are the only common frame the panes share — each has its
/// own origin and its own DPI scale, so every pane converts on its own.
/// </summary>
public sealed class OverviewDragCoordinator
{
    private readonly List<OverviewWindow> _panes = new();

    public OverviewDragSession? Session { get; private set; }

    public void Register(OverviewWindow pane)
    {
        if (!_panes.Contains(pane)) _panes.Add(pane);
    }

    public void Unregister(OverviewWindow pane)
    {
        _panes.Remove(pane);
        if (Session is not null && ReferenceEquals(Session.SourcePane, pane)) End();
    }

    public void Begin(OverviewDragSession session)
    {
        Session = session;
    }

    /// <summary>Pushes the current cursor position (physical screen pixels) to every pane.</summary>
    public void DragOver(int screenX, int screenY)
    {
        if (Session is null) return;

        foreach (var pane in _panes.ToList())
        {
            pane.UpdateDragFeedback(Session, screenX, screenY);
        }
    }

    /// <summary>The pane whose monitor contains the point, or null if the pointer is between/outside monitors.</summary>
    public OverviewWindow? PaneAt(int screenX, int screenY) =>
        _panes.FirstOrDefault(p => p.ContainsScreenPoint(screenX, screenY));

    public void End()
    {
        Session = null;

        foreach (var pane in _panes.ToList())
        {
            pane.ClearDragFeedback();
        }
    }

    /// <summary>Rebuilds every pane after a drop — a cross-monitor move changes two of them.</summary>
    public void RefreshAll()
    {
        foreach (var pane in _panes.ToList())
        {
            pane.RefreshFromCoordinator();
        }
    }
}
