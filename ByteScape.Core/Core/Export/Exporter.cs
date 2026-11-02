using System.Text;
using ByteScape.Core.Model;
using ByteScape.Core.Snapshot;

namespace ByteScape.Core.Export;

/// <summary>
/// Runs an export configuration over the currently zoomed + filtered view.
/// Elements are visited depth first; header/footer see the export root only.
/// </summary>
public sealed class Exporter
{
    private readonly ExportConfig _config;
    private readonly ContainerNode _root;
    private readonly Func<FsNode, bool> _include;
    private readonly object _sync;
    private readonly Func<FsNode, long> _sizeOf;

    public Exporter(ExportConfig config, ContainerNode exportRoot, Func<FsNode, bool> include, object treeSync, Func<FsNode, long>? sizeOf = null)
    {
        _config = config;
        _root = exportRoot;
        _include = include;
        _sync = treeSync;
        _sizeOf = sizeOf ?? (n => n.DiskSize);
    }

    /// <summary>Ordered children according to block / fine sorting.</summary>
    private IEnumerable<FsNode> Ordered(ContainerNode c)
    {
        var kids = c.Children.Where(k => !k.IsSynthetic && _include(k)).ToList();
        IEnumerable<FsNode> Fine(IEnumerable<FsNode> src)
        {
            switch (_config.FineSorting)
            {
                // descending = a stable ascending sort, reversed: ties come out in reverse scan order (the .sns format keeps that order)
                case FineSorting.Size: return _config.FineSortingDescending ? src.Reverse().OrderByDescending(_sizeOf) : src.OrderBy(_sizeOf);
                case FineSorting.Name: return _config.FineSortingDescending ? src.Reverse().OrderByDescending(k => k.Name, StringComparer.OrdinalIgnoreCase) : src.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase);
                default: return src;
            }
        }
        switch (_config.BlockSorting)
        {
            case BlockSorting.Files: return Fine(kids.Where(k => k is FileNode)).Concat(Fine(kids.Where(k => k is not FileNode)));
            case BlockSorting.Folders: return Fine(kids.Where(k => k is not FileNode)).Concat(Fine(kids.Where(k => k is FileNode)));
            default: return Fine(kids);
        }
    }

    /// <summary>Text export. Returns false when the output would be empty (caller may warn first via <see cref="WouldBeEmpty"/>).</summary>
    public void ExportText(TextWriter writer, CancellationToken token, Action<int>? progress = null, int? maxElements = null)
    {
        var header = new Script(_config.HeaderFormat);
        var detail = new Script(_config.DetailFormat);
        var footer = new Script(_config.FooterFormat);
        var counters = new ExportCounters();
        var now = DateTime.UtcNow;
        int visited = 0;

        // The tree lock is taken per directory (child list snapshot), not for the whole export: a running
        // scan and the UI refresh tick are never blocked for the seconds a 500k line export takes.
        var rootCtx = new ScriptContext { Node = _root, ExportRoot = _root, Counters = counters, Include = _include, NowUtc = now, SizeOf = _sizeOf };
        writer.Write(header.Evaluate(rootCtx));
        counters.Advance(_root);

        var stack = new Stack<(FsNode node, bool enter)>();
        stack.Push((_root, true));
        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (node, _) = stack.Pop();
            var ctx = new ScriptContext { Node = node, ExportRoot = _root, Counters = counters, Include = _include, NowUtc = now, SizeOf = _sizeOf };
            writer.Write(detail.Evaluate(ctx));
            counters.Advance(node);
            visited++;
            if (maxElements != null && visited >= maxElements) break;
            if ((visited & 0xFF) == 0) progress?.Invoke(visited);
            if (node is ContainerNode c)
            {
                List<FsNode> kids;
                lock (_sync) kids = Ordered(c).ToList();
                for (int i = kids.Count - 1; i >= 0; i--) stack.Push((kids[i], true));
                counters.ResetLocal();
            }
        }
        var footCtx = new ScriptContext { Node = _root, ExportRoot = _root, Counters = counters, Include = _include, NowUtc = now, SizeOf = _sizeOf };
        writer.Write(footer.Evaluate(footCtx));
    }

    public string Preview(int maxElements = 40)
    {
        var sb = new StringBuilder();
        using var w = new StringWriter(sb);
        try { ExportText(w, CancellationToken.None, null, maxElements); }
        catch (Exception ex) { sb.Append("\r\n*** ").Append(ex.Message); }
        return sb.ToString();
    }

    public bool WouldBeEmpty()
    {
        if (_config.IsBinarySnapshot) return false;
        return string.IsNullOrEmpty(Preview(50).Trim());
    }

    public void ExportBinary(string path, CancellationToken token, Action<int>? progress = null)
    {
        lock (_sync) SnsFormat.Write(path, _root, _include, progress, token);
    }
}
