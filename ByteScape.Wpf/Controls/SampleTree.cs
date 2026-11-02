using ByteScape.Core.Model;
using ByteScape.Core.Scanning;

namespace ByteScape.Wpf.Controls;

/// <summary>Small in-memory disk-like tree used by the configuration preview (rendered by the real <see cref="TreemapView"/>).</summary>
public static class SampleTree
{
    public static ScanTree Create()
    {
        const long MB = 1024 * 1024;
        var root = new DriveNode("C:\\") { IsDrive = true, TotalBytes = 6000 * MB, FreeBytes = 700 * MB, Enumerated = true, ScanComplete = true };
        FolderNode Folder(ContainerNode p, string name) { var f = new FolderNode(name) { Enumerated = true, ScanComplete = true }; p.AddChild(f); return f; }
        void File(ContainerNode p, string name, long mb, TagColor tag = TagColor.None)
        {
            var f = new FileNode(name) { LogicalSize = mb * MB, DiskSize = mb * MB, Tag = tag, Attributes = FileAttributes.Archive, Created = DateTime.UtcNow.AddDays(-400), Modified = DateTime.UtcNow.AddDays(-30), Accessed = DateTime.UtcNow.AddDays(-2) };
            p.AddChild(f);
        }
        var users = Folder(root, "Users");
        var pics = Folder(users, "Pictures");
        File(pics, "holiday.jpg", 420, TagColor.Red); File(pics, "family.png", 260); File(pics, "scan.tif", 140);
        var music = Folder(users, "Music");
        File(music, "album.flac", 380); File(music, "song.mp3", 90, TagColor.Blue); File(music, "clip.mp3", 70);
        var docs = Folder(users, "Documents");
        File(docs, "thesis.docx", 120); File(docs, "notes.txt", 30);
        for (int i = 0; i < 14; i++) File(docs, "tiny" + i + ".txt", 4);
        var win = Folder(root, "Windows");
        var sys = Folder(win, "System32");
        File(sys, "kernel.dll", 300); File(sys, "shell.exe", 180, TagColor.Green); File(sys, "driver.sys", 110);
        var inst = Folder(win, "Installer");
        File(inst, "setup.msi", 260); File(inst, "pack.zip", 200, TagColor.Yellow);
        File(root, "pagefile.sys", 520);
        File(root, "movie.mkv", 640);
        return new ScanTree(root, live: false);
    }
}
