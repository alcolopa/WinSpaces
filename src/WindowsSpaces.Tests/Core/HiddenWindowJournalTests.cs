using WindowsSpaces.Platform.Win32;
using Xunit;

namespace WindowsSpaces.Tests.Core;

public class HiddenWindowJournalTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"journal-{Guid.NewGuid():N}.txt");

    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    [Fact]
    public void EntriesSurviveIntoNextInstance()
    {
        var first = new HiddenWindowJournal(_path);
        first.Add(new HiddenWindowJournal.Entry(100, 42, "Chrome_WidgetWin_1"));
        first.Add(new HiddenWindowJournal.Entry(200, 43, "Notepad|odd"));

        var stale = new HiddenWindowJournal(_path).TakeStaleEntries();

        Assert.Equal(2, stale.Count);
        Assert.Contains(new HiddenWindowJournal.Entry(200, 43, "Notepad|odd"), stale);
    }

    [Fact]
    public void ShownWindowsAreRemoved_AndFileDeletedWhenEmpty()
    {
        var journal = new HiddenWindowJournal(_path);
        journal.Add(new HiddenWindowJournal.Entry(100, 42, "A"));
        journal.Remove(100);

        Assert.False(File.Exists(_path));
        Assert.Empty(new HiddenWindowJournal(_path).TakeStaleEntries());
    }

    [Fact]
    public void CorruptLinesAreSkipped()
    {
        File.WriteAllText(_path, "garbage\n100|42|A\nx|y|z");
        var stale = new HiddenWindowJournal(_path).TakeStaleEntries();
        Assert.Single(stale);
    }
}
