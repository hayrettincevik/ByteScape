namespace ByteScape.Core.Cleanup;

/// <summary>What a file is, by its name: what deleting it means differs (a document is gone for good, an installer can be downloaded again).</summary>
public enum FileKind
{
    Other,
    /// <summary>Programs and their libraries (.exe, .dll, .sys): a program may stop working.</summary>
    Program,
    /// <summary>Installation packages (.msi, .msix, setup.exe, .iso): the installed program keeps working without them.</summary>
    Installer,
    /// <summary>Documents, spreadsheets, text, e-books.</summary>
    Document,
    Photo,
    Video,
    Audio,
    Archive,
    /// <summary>Databases and mailboxes (.pst, .sqlite, .mdf): the data in them is lost.</summary>
    Database,
    /// <summary>Virtual machine disks (.vhdx, .vmdk): the machine is lost.</summary>
    VirtualDisk,
    Backup,
    /// <summary>Keys, certificates, password databases, crypto wallets: cannot be recovered.</summary>
    Secret,
    /// <summary>Temporary files (.tmp, Office's ~$ owner files).</summary>
    Temporary,
    /// <summary>Unfinished downloads (.crdownload, .part).</summary>
    PartialDownload,
    Log,
    /// <summary>Crash dumps (.dmp).</summary>
    Dump,
    /// <summary>Source code and project files.</summary>
    Code,
}

public static class FileKinds
{
    private static readonly Dictionary<string, FileKind> ByExtension = new(StringComparer.OrdinalIgnoreCase);

    static FileKinds()
    {
        void Add(FileKind k, string exts) { foreach (var e in exts.Split(' ', StringSplitOptions.RemoveEmptyEntries)) ByExtension[e] = k; }
        Add(FileKind.Program, "exe dll sys drv ocx cpl msc efi mui scr com ax winmd node pyd so");
        Add(FileKind.Installer, "msi msp msu msix msixbundle appx appxbundle appinstaller iso img cab");
        Add(FileKind.Document, "doc docx docm dot dotx xls xlsx xlsm xlsb ppt pptx pptm odt ods odp rtf txt md pdf epub mobi djvu xps oxps pages numbers key one vsd vsdx pub tex csv");
        Add(FileKind.Photo, "jpg jpeg png gif bmp tif tiff heic heif webp avif raw cr2 cr3 nef arw dng orf rw2 raf psd psb xcf ai svg ico");
        Add(FileKind.Video, "mp4 m4v mov avi mkv wmv flv webm mpg mpeg m2ts mts ts 3gp vob");
        Add(FileKind.Audio, "mp3 flac wav wma aac m4a ogg opus aiff alac mid midi");
        Add(FileKind.Archive, "zip 7z rar tar gz tgz bz2 xz zst lz4");
        Add(FileKind.Database, "pst ost mdb accdb sqlite sqlite3 db mdf ldf ndf frm ibd myd myi dbf fdb gdb kdb realm");
        Add(FileKind.VirtualDisk, "vhd vhdx avhd avhdx vmdk vdi qcow qcow2 vmem vmsn vmss hdd");
        Add(FileKind.Backup, "bak bkf wbcat tib tibx vbk vib mrimg");
        Add(FileKind.Secret, "pfx p12 pem key kdbx kdb keychain ppk gpg asc jks keystore ovpn");
        Add(FileKind.Temporary, "tmp temp");
        Add(FileKind.PartialDownload, "crdownload part partial download opdownload !ut");
        Add(FileKind.Log, "log etl evtx");
        Add(FileKind.Dump, "dmp mdmp hdmp");
        Add(FileKind.Code, "cs vb fs cpp cc c h hpp java kt go rs py rb php js ts jsx tsx swift m sql sln csproj vbproj fsproj vcxproj gradle ipynb");
    }

    /// <summary>The kind of a file from its name (a few names count more than their extension: wallet.dat, ~$ owner files, setup.exe).</summary>
    public static FileKind Of(string name)
    {
        if (name.StartsWith("~$", StringComparison.Ordinal)) return FileKind.Temporary;
        if (name.Equals("wallet.dat", StringComparison.OrdinalIgnoreCase) || name.Equals("id_rsa", StringComparison.OrdinalIgnoreCase)
            || name.Equals("id_ed25519", StringComparison.OrdinalIgnoreCase) || name.Equals("id_ecdsa", StringComparison.OrdinalIgnoreCase)) return FileKind.Secret;
        int dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return FileKind.Other;
        var ext = name.Substring(dot + 1);
        if (!ByExtension.TryGetValue(ext, out var k)) return FileKind.Other;
        if (k == FileKind.Program && ext.Equals("exe", StringComparison.OrdinalIgnoreCase) && LooksLikeInstaller(name)) return FileKind.Installer;
        return k;
    }

    /// <summary>"Setup.exe", "Firefox Installer.exe", "vc_redist.x64.exe", "python-3.12.1-amd64.exe"...</summary>
    internal static bool LooksLikeInstaller(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("setup") || n.Contains("install") || n.Contains("redist") || n.Contains("_x64.exe") || n.Contains("-amd64") || n.Contains("-x64.exe")
               || n.Contains("win64") || n.Contains("win-x64") || n.Contains("update") && n.EndsWith(".exe");
    }

    /// <summary>A count of files of a kind in the interface language: "12 photos and images".</summary>
    public static string Count(FileKind k, long n) => k switch
    {
        FileKind.Program => P(n, "{0:N0} program file", "{0:N0} program files"),
        FileKind.Installer => P(n, "{0:N0} installation file", "{0:N0} installation files"),
        FileKind.Document => P(n, "{0:N0} document", "{0:N0} documents"),
        FileKind.Photo => P(n, "{0:N0} photo or image", "{0:N0} photos and images"),
        FileKind.Video => P(n, "{0:N0} video", "{0:N0} videos"),
        FileKind.Audio => P(n, "{0:N0} audio file", "{0:N0} audio files"),
        FileKind.Archive => P(n, "{0:N0} archive", "{0:N0} archives"),
        FileKind.Database => P(n, "{0:N0} database or mailbox", "{0:N0} databases or mailboxes"),
        FileKind.VirtualDisk => P(n, "{0:N0} virtual machine disk", "{0:N0} virtual machine disks"),
        FileKind.Backup => P(n, "{0:N0} backup", "{0:N0} backups"),
        FileKind.Secret => P(n, "{0:N0} key, certificate, password database or wallet", "{0:N0} keys, certificates, password databases or wallets"),
        FileKind.Temporary => P(n, "{0:N0} temporary file", "{0:N0} temporary files"),
        FileKind.PartialDownload => P(n, "{0:N0} unfinished download", "{0:N0} unfinished downloads"),
        FileKind.Log => P(n, "{0:N0} log", "{0:N0} logs"),
        FileKind.Dump => P(n, "{0:N0} crash dump", "{0:N0} crash dumps"),
        FileKind.Code => P(n, "{0:N0} source file", "{0:N0} source files"),
        _ => P(n, "{0:N0} file", "{0:N0} files"),
    };

    /// <summary>The name of one file of a kind, for "Safe to delete: temporary file".</summary>
    public static string Name(FileKind k) => T(k switch
    {
        FileKind.Program => "program file",
        FileKind.Installer => "installation file",
        FileKind.Document => "document",
        FileKind.Photo => "photo or image",
        FileKind.Video => "video",
        FileKind.Audio => "audio file",
        FileKind.Archive => "archive",
        FileKind.Database => "database or mailbox",
        FileKind.VirtualDisk => "virtual machine disk",
        FileKind.Backup => "backup",
        FileKind.Secret => "key, certificate, password database or wallet",
        FileKind.Temporary => "temporary file",
        FileKind.PartialDownload => "unfinished download",
        FileKind.Log => "log",
        FileKind.Dump => "crash dump",
        FileKind.Code => "source file",
        _ => "file",
    });

    /// <summary>What deleting a file of this kind means, in one sentence.</summary>
    public static string Meaning(FileKind k) => T(k switch
    {
        FileKind.Program => "A program file: the program it belongs to may stop working.",
        FileKind.Installer => "An installation file: the installed program keeps working without it; to install again you download it again.",
        FileKind.Document => "A document: if there is no other copy it is gone for good.",
        FileKind.Photo => "A photo or image: if there is no other copy it is gone for good.",
        FileKind.Video => "A video: if there is no other copy it is gone for good.",
        FileKind.Audio => "An audio file: if there is no other copy it is gone for good.",
        FileKind.Archive => "An archive: everything packed in it goes with it.",
        FileKind.Database => "A database or mailbox: the data (mails, records) in it is lost; an Outlook .ost is downloaded again, a .pst is not.",
        FileKind.VirtualDisk => "A virtual machine disk: the machine and everything inside it is lost.",
        FileKind.Backup => "A backup: you lose the copy you would restore from.",
        FileKind.Secret => "A key, certificate, password database or wallet: it cannot be recreated; what it protects may be lost for good.",
        FileKind.Temporary => "A temporary file: programs make and forget them; safe unless a program has it open.",
        FileKind.PartialDownload => "An unfinished download: the browser starts it again if you download the file again.",
        FileKind.Log => "A log: programs write it for troubleshooting; nothing else is lost.",
        FileKind.Dump => "A crash dump: only useful to report the crash; nothing else is lost.",
        FileKind.Code => "Source code: if it is not in a repository or backed up, the work is lost.",
        _ => "",
    });

    /// <summary>Kinds whose loss cannot be undone by downloading or rebuilding: worth a word before deleting a folder holding them.</summary>
    public static bool IsIrreplaceable(FileKind k) => k is FileKind.Document or FileKind.Photo or FileKind.Video or FileKind.Audio
        or FileKind.Database or FileKind.VirtualDisk or FileKind.Backup or FileKind.Secret or FileKind.Code;
}
