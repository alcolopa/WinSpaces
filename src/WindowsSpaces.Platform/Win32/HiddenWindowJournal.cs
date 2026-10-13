using System.Text;

namespace WindowsSpaces.Platform.Win32;

/// <summary>
/// Durable record of the windows this process has hidden. If the app is
/// killed (Task Manager, power loss, native crash) while spaces are hidden,
/// nothing else knows those windows exist; the next launch reads this file
/// and shows them again so no window is ever permanently lost.
///
/// An entry is hwnd + owning process id + window class, so a recycled handle
/// that now belongs to a different window is never "recovered" by mistake.
/// All I/O is best effort: the journal is a safety net and must never be the
/// reason a hide or show fails.
/// </summary>
public sealed class HiddenWindowJournal
{
    public readonly record struct Entry(long Hwnd, uint ProcessId, string WindowClass);

    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<long, Entry> _entries = new();

    public HiddenWindowJournal(string path) => _path = path;

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowsSpaces", "hidden-windows.txt");

    public void Add(Entry entry)
    {
        lock (_lock)
        {
            _entries[entry.Hwnd] = entry;
            Persist();
        }
    }

    public void Remove(long hwnd)
    {
        lock (_lock)
        {
            if (_entries.Remove(hwnd))
                Persist();
        }
    }

    /// <summary>Reads entries left by a previous run and clears the file.</summary>
    public IReadOnlyList<Entry> TakeStaleEntries()
    {
        lock (_lock)
        {
            var result = new List<Entry>();
            try
            {
                if (File.Exists(_path))
                {
                    foreach (var line in File.ReadAllLines(_path))
                    {
                        if (TryParse(line, out var entry))
                            result.Add(entry);
                    }
                }
            }
            catch (Exception)
            {
                // Unreadable journal: nothing recoverable from it.
            }

            _entries.Clear();
            Persist();
            return result;
        }
    }

    internal static string Format(Entry e) => $"{e.Hwnd}|{e.ProcessId}|{e.WindowClass}";

    internal static bool TryParse(string line, out Entry entry)
    {
        entry = default;
        var parts = line.Split('|', 3);
        if (parts.Length != 3 || !long.TryParse(parts[0], out var hwnd) || !uint.TryParse(parts[1], out var pid))
            return false;
        entry = new Entry(hwnd, pid, parts[2]);
        return true;
    }

    private void Persist()
    {
        try
        {
            if (_entries.Count == 0)
            {
                if (File.Exists(_path)) File.Delete(_path);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, string.Join('\n', _entries.Values.Select(Format)), Encoding.UTF8);
        }
        catch (Exception)
        {
            // Best effort; see type remarks.
        }
    }
}
