using ByteScape.Core.Compare;
using ByteScape.Core.Model;
using ByteScape.Core.Snapshot;
using ByteScape.Wpf.Controls;
using Xunit;

namespace ByteScape.Tests;

/// <summary>Snapshot comparison: alignment, change classification, subtree sums, overlay data.</summary>
public class CompareTests
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "bytescape-tests", "compare");
    static CompareTests() => Directory.CreateDirectory(Dir);

    private static FileNode File(ContainerNode p, string name, long bytes) { var f = new FileNode(name) { LogicalSize = bytes, DiskSize = bytes }; p.AddChild(f); return f; }
    private static FolderNode Folder(ContainerNode p, string name) { var f = new FolderNode(name) { Enumerated = true, ScanComplete = true }; p.AddChild(f); return f; }

    [Fact]
    public void Compare_ClassifiesGrownShrunkAddedRemoved_AndSumsFolders()
    {
        const long MB = 1024 * 1024;
        var before = SampleTree.Create().Root;                      // the "snapshot"
        var after = SampleTree.Create().Root;                       // "now", then mutate it
        var users = (ContainerNode)after.FindChild("Users")!;
        var pics = (ContainerNode)users.FindChild("Pictures")!;
        var holiday = (FileNode)pics.FindChild("holiday.jpg")!;
        pics.ResizeChild(holiday, 500 * MB, 500 * MB);               // grown by 80 MB
        var music = (ContainerNode)users.FindChild("Music")!;
        music.RemoveChild(music.FindChild("clip.mp3")!);            // removed 70 MB
        File(users, "new.iso", 300 * MB);                            // added
        var newDir = Folder(users, "Videos"); File(newDir, "a.mkv", 100 * MB); File(newDir, "b.mkv", 50 * MB);   // added folder
        var win = (ContainerNode)after.FindChild("Windows")!;
        var sys = (ContainerNode)win.FindChild("System32")!;
        sys.ResizeChild(sys.FindChild("kernel.dll")!, 200 * MB, 200 * MB);   // shrunk by 100 MB

        var diff = TreeDiff.Compare(before, "before.sns", new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc), after, null);
        Assert.Equal(before.DiskSize, diff.OldTotal);
        Assert.Equal(after.DiskSize, diff.NewTotal);
        Assert.Equal((80 - 70 + 300 + 150 - 100) * MB, diff.Delta);
        DiffEntry E(string path) => diff.Entries.Single(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ChangeKind.Grown, E(@"C:\Users\Pictures\holiday.jpg").Kind); Assert.Equal(80 * MB, E(@"C:\Users\Pictures\holiday.jpg").Delta);
        Assert.Equal(ChangeKind.Grown, E(@"C:\Users\Pictures").Kind); Assert.Equal(80 * MB, E(@"C:\Users\Pictures").Delta);
        Assert.Equal(ChangeKind.Removed, E(@"C:\Users\Music\clip.mp3").Kind); Assert.Equal(-70 * MB, E(@"C:\Users\Music\clip.mp3").Delta); Assert.Null(E(@"C:\Users\Music\clip.mp3").Current);
        Assert.Equal(ChangeKind.Shrunk, E(@"C:\Users\Music").Kind);
        Assert.Equal(ChangeKind.Added, E(@"C:\Users\new.iso").Kind); Assert.Equal(300 * MB, E(@"C:\Users\new.iso").Delta);
        Assert.Equal(ChangeKind.Added, E(@"C:\Users\Videos").Kind); Assert.Equal(150 * MB, E(@"C:\Users\Videos").Delta); Assert.True(E(@"C:\Users\Videos").IsFolder);
        Assert.DoesNotContain(diff.Entries, e => e.Path.EndsWith("a.mkv"));    // children of an added folder are not listed separately
        Assert.Equal(ChangeKind.Shrunk, E(@"C:\Windows\System32\kernel.dll").Kind); Assert.Equal(-100 * MB, E(@"C:\Windows\System32\kernel.dll").Delta);
        Assert.Equal(ChangeKind.Shrunk, E(@"C:\Windows").Kind); Assert.Equal(-100 * MB, E(@"C:\Windows").Delta);
        Assert.Equal((80 - 70 + 300 + 150) * MB, E(@"C:\Users").Delta);
        Assert.DoesNotContain(diff.Entries, e => e.Path.EndsWith("pagefile.sys"));   // unchanged files are not listed
        Assert.DoesNotContain(diff.Entries, e => e.Path == @"C:\");                    // the root itself is the summary, not an entry
        // sorted by absolute change, biggest first
        Assert.Equal(diff.Entries.Select(e => e.AbsDelta).OrderByDescending(x => x), diff.Entries.Select(e => e.AbsDelta));
        // overlay data: added nodes (including children of added folders) and per-node deltas
        Assert.Contains(newDir, diff.AddedNodes);
        Assert.Contains(newDir.FindChild("a.mkv")!, diff.AddedNodes);
        Assert.Equal(80 * MB, diff.DeltaOf[holiday]);
        Assert.Equal(-100 * MB, diff.DeltaOf[win]);
        Assert.False(diff.DeltaOf.ContainsKey(after.FindChild("pagefile.sys")!));
        Assert.Equal(2, diff.AddedCount); Assert.Equal(1, diff.RemovedCount);
        Assert.Equal(450 * MB, diff.AddedBytes); Assert.Equal(70 * MB, diff.RemovedBytes);
        Assert.Equal(460 * MB, diff.MaxAbsDelta);                 // the Users folder: biggest single change, folders included
    }

    [Fact]
    public void Align_FindsTheSubtreeMatchingTheCurrentView()
    {
        var snap = SampleTree.Create().Root;                          // C:\
        Assert.Same(snap, TreeDiff.Align(snap, @"C:\"));
        Assert.Same(snap, TreeDiff.Align(snap, "C:"));
        var users = TreeDiff.Align(snap, @"C:\Users");
        Assert.NotNull(users); Assert.Equal("Users", users!.Name);
        Assert.Equal("Music", TreeDiff.Align(snap, @"c:\users\music\")!.Name);
        Assert.Null(TreeDiff.Align(snap, @"D:\Data"));
        Assert.Null(TreeDiff.Align(snap, @"C:\Users\Nope"));
    }

    [Fact]
    public void Compare_WorksWithASnapshotFileRoundTrip()
    {
        var tree = SampleTree.Create();
        var file = Path.Combine(Dir, "baseline.sns");
        SnsFormat.Write(file, tree.Root, _ => true);
        var baseline = SnsFormat.Read(file);
        // no change at all
        var same = TreeDiff.Compare(TreeDiff.Align(baseline, tree.Root.FullPath)!, file, DateTime.UtcNow, tree.Root, tree.Sync);
        Assert.Empty(same.Entries); Assert.Equal(0, same.Delta); Assert.Empty(same.DeltaOf);
        // compare a sub folder view against the full-drive snapshot
        var users = (ContainerNode)tree.Root.FindChild("Users")!;
        File(users, "big.bin", 1L << 30);
        var part = TreeDiff.Compare(TreeDiff.Align(baseline, users.FullPath)!, file, DateTime.UtcNow, users, tree.Sync);
        Assert.Single(part.Entries);
        Assert.Equal(ChangeKind.Added, part.Entries[0].Kind);
        Assert.Equal(1L << 30, part.Delta);
    }
}
