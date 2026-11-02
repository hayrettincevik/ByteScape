using System.Buffers.Binary;
using System.Text;
using ByteScape.Core.Config;
using ByteScape.Core.Util;
using ByteScape.Interop;

namespace ByteScape.Core.Scanning;

/// <summary>What kind of storage a path is on, as far as scanning is concerned.</summary>
public enum DriveKind { Unknown, Hdd, SataSsd, Nvme, UsbFlash, Network, Optical }

/// <summary>
/// How a drive is scanned: folders listed at the same time, the order folders are walked in, the readers of the NTFS master
/// file table, the size of the listing buffer, and whether the full scan steps aside while a folder is rescanned.
/// </summary>
public sealed record ScanStrategy(DriveKind Kind, int Workers, bool DiskOrder, int MftReaders, int ListBufferBytes, bool YieldToSubScans, string Product, bool Overridden)
{
    /// <summary>For the status line: "SSD, 4 folders in parallel", "HDD, folders in disk order".</summary>
    public string Short => Kind switch
    {
        DriveKind.Hdd => T(DiskOrder ? "HDD, folders in disk order" : "HDD, one folder at a time"),
        DriveKind.Optical => T("CD / DVD, one folder at a time"),
        _ => KindName(Kind) + ", " + (Workers == 1 ? T("one folder at a time") : F($"{Workers} folders in parallel")),
    };

    public static string KindName(DriveKind k) => k switch
    {
        DriveKind.Hdd => "HDD", DriveKind.SataSsd => "SSD", DriveKind.Nvme => "NVMe SSD", DriveKind.UsbFlash => T("USB / memory card"),
        DriveKind.Network => T("network"), DriveKind.Optical => "CD / DVD", _ => T("drive"),
    };
}

/// <summary>
/// Picks a scan strategy per drive. The kind comes from the disk itself (no administrator rights: a zero-access volume
/// handle answers IOCTL_STORAGE_QUERY_PROPERTY): the seek penalty tells spinning disks from flash, the bus type NVMe and
/// USB. Spinning disks are walked one folder at a time, in the order the folders' records sit in the master file table
/// (the head sweeps across the table instead of jumping), and the full scan pauses while a folder is rescanned; SSDs are
/// listed in parallel, NVMe drives with deeper queues; USB flash and network paths get a larger listing buffer (fewer
/// round trips). The configuration can force a kind and the number of folders listed at once.
/// </summary>
public static class DriveProfile
{
    // STORAGE_BUS_TYPE values
    internal const int BusScsi = 1, BusAta = 3, BusUsb = 7, BusRaid = 8, BusSata = 11, BusSd = 12, BusMmc = 13, BusVirtual = 14, BusFileBackedVirtual = 15, BusNvme = 17;

    private static readonly Dictionary<string, (DriveKind Kind, string Product)> Detected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tests: answer for a drive letter without asking the disk.</summary>
    internal static Func<string, (DriveKind Kind, string Product)?>? Fake;

    /// <summary>The kind of the disk under <paramref name="path"/> and its product name (cached per drive letter).</summary>
    public static (DriveKind Kind, string Product) Detect(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return (DriveKind.Network, "");
        if (path.Length < 2 || path[1] != ':') return (DriveKind.Unknown, "");
        string drive = path.Substring(0, 2).ToUpperInvariant();
        if (Fake?.Invoke(drive) is { } fake) return fake;
        lock (Detected)
        {
            if (Detected.TryGetValue(drive, out var known)) return known;
            var desc = Native.QueryStorageDevice(drive);
            var parsed = desc == null ? null : ParseDeviceDescriptor(desc);
            bool? seek = Native.IncursSeekPenalty(drive);
            var type = DriveType.Unknown;
            try { type = new DriveInfo(drive).DriveType; } catch { }
            var kind = type switch
            {
                DriveType.Network => DriveKind.Network,
                DriveType.CDRom => DriveKind.Optical,          // removable media, but a head that seeks very slowly
                _ => KindOf(seek, parsed?.BusType ?? -1, parsed?.Removable ?? false),
            };
            var result = (kind, parsed?.Product ?? "");
            Detected[drive] = result;
            Log.Debug($"Drive {drive}: {ScanStrategy.KindName(kind)} ({(result.Item2.Length > 0 ? result.Item2 + ", " : "")}bus {parsed?.BusType.ToString() ?? "?"}, seek penalty {seek?.ToString() ?? "unknown"})");
            return result;
        }
    }

    /// <summary>The kind from what the disk says (-1: bus type unknown).</summary>
    internal static DriveKind KindOf(bool? seekPenalty, int busType, bool removable)
    {
        if (busType == BusNvme) return DriveKind.Nvme;
        if (seekPenalty == true) return DriveKind.Hdd;                  // USB hard disks included
        if (busType is BusUsb or BusSd or BusMmc) return DriveKind.UsbFlash;
        if (seekPenalty == false) return DriveKind.SataSsd;             // SATA, SAS, RAID and virtual disks without a head
        return removable ? DriveKind.UsbFlash : DriveKind.Unknown;
    }

    /// <summary>Bus type, removable flag and "vendor product" from a STORAGE_DEVICE_DESCRIPTOR.</summary>
    internal static (int BusType, bool Removable, string Product)? ParseDeviceDescriptor(byte[] d)
    {
        if (d.Length < 36) return null;
        bool removable = d[10] != 0;
        int vendorOfs = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(12));
        int productOfs = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(16));
        int bus = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(28));
        string Ascii(int ofs)
        {
            if (ofs <= 0 || ofs >= d.Length) return "";
            int end = Array.IndexOf(d, (byte)0, ofs);
            return Encoding.ASCII.GetString(d, ofs, (end < 0 ? d.Length : end) - ofs).Trim();
        }
        var product = (Ascii(vendorOfs) + " " + Ascii(productOfs)).Trim();
        return (bus, removable, product);
    }

    /// <summary>The strategy for a kind of drive on a machine with <paramref name="cores"/> processor cores.</summary>
    public static ScanStrategy StrategyFor(DriveKind kind, int cores, string product = "", bool overridden = false)
    {
        const int BigBuffer = 64 << 10;
        return kind switch
        {
            // a head that seeks: one folder at a time, in master file table order; no competing readers
            DriveKind.Hdd => new(kind, 1, true, 1, BigBuffer, true, product, overridden),
            // NVMe answers many requests at once: deeper queues than SATA
            DriveKind.Nvme => new(kind, Math.Clamp(cores * 2, 4, 16), false, 8, 0, false, product, overridden),
            // USB sticks and memory cards: few requests at once, fewer bigger listing calls
            DriveKind.UsbFlash => new(kind, 2, false, 2, BigBuffer, false, product, overridden),
            // a very slow seek, no NTFS: one folder at a time, big listing calls
            DriveKind.Optical => new(kind, 1, false, 1, BigBuffer, true, product, overridden),
            // latency bound: many folders at once, each listing call a round trip
            DriveKind.Network => new(kind, 8, false, 1, BigBuffer, false, product, overridden),
            // SATA SSD, or unknown: one folder per core
            _ => new(kind, Math.Clamp(cores, 2, 8), false, 4, 0, false, product, overridden),
        };
    }

    /// <summary>The strategy for a scan of <paramref name="path"/>: the configuration's drive kind and folder count win over what the disk says.</summary>
    public static ScanStrategy For(string path)
    {
        var cfg = AppConfig.Current;
        var (kind, product) = Detect(path);
        bool overridden = false;
        if (cfg.ScanDriveKind is >= 1 and <= 4 && kind is not (DriveKind.Network or DriveKind.Optical))
        {
            kind = cfg.ScanDriveKind switch { 1 => DriveKind.Hdd, 2 => DriveKind.SataSsd, 3 => DriveKind.Nvme, _ => DriveKind.UsbFlash };
            overridden = true;
        }
        var s = StrategyFor(kind, Environment.ProcessorCount, product, overridden);
        if (cfg.ScanThreads > 0) s = s with { Workers = Math.Min(cfg.ScanThreads, 32), DiskOrder = s.DiskOrder && cfg.ScanThreads == 1, Overridden = true };
        return s;
    }
}

/// <summary>
/// Folders to list in master file table order, the way an elevator serves floors (C-SCAN): the lowest record number ahead
/// of the head first; folders found behind it wait for the next sweep. On a spinning disk the head then crosses the table
/// in one direction instead of jumping back and forth between the records of the folders a breadth first walk would take.
/// </summary>
internal sealed class DiskOrderQueue<T>
{
    private PriorityQueue<T, long> _ahead = new(), _behind = new();
    private long _head = -1;

    public int Count => _ahead.Count + _behind.Count;

    /// <summary>Queues an item at a record number (unknown: 0 or less, taken right after the current one).</summary>
    public void Enqueue(T item, long record)
    {
        if (record <= 0) record = Math.Max(_head, 0);
        (record >= _head ? _ahead : _behind).Enqueue(item, record);
    }

    public bool TryDequeue(out T item)
    {
        if (_ahead.Count == 0) (_ahead, _behind) = (_behind, _ahead);   // next sweep from the start of the table
        if (!_ahead.TryDequeue(out item!, out var record)) return false;
        _head = record;
        return true;
    }
}
