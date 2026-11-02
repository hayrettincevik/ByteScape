using ByteScape.Core.Model;
using ByteScape.Core.Util;

namespace ByteScape.Core.Cleanup;

/// <summary>One element the user chose to delete: the verdict, what actually goes (the element, or what is inside it), what stands in the way.</summary>
public sealed class PlanItem
{
    public required Assessment Assessment { get; init; }
    public FsNode Node => Assessment.Node;
    /// <summary>What is deleted: the element itself, or (a folder that must stay) the elements inside it.</summary>
    public List<(FsNode Node, string Path, long Bytes)> Targets { get; } = new();
    public long Bytes { get; internal set; }
    /// <summary>Elements inside left alone because they changed recently (a program may be using them).</summary>
    public int KeptRecent { get; internal set; }
    /// <summary>What trying the rights found (null until <see cref="DeletePlan.Probe"/> ran).</summary>
    public ProbeSummary? Probe { get; internal set; }
    public bool Deletable => !Assessment.Blocked && Targets.Count > 0;
}

/// <summary>
/// What a delete would do: the chosen elements (an element inside another chosen one is dropped), each with its verdict;
/// protected ones are listed but never deleted; folders that must stay (Temp) give their content instead. Knows the
/// Recycle Bins involved (none on USB sticks and network drives; a size limit on the others) and, after
/// <see cref="Probe"/>, what needs administrator rights, is open in a program or is protected by Windows.
/// </summary>
public sealed class DeletePlan
{
    public List<PlanItem> Items { get; } = new();
    public IEnumerable<PlanItem> Deletable => Items.Where(i => i.Deletable);
    public IEnumerable<PlanItem> Protected => Items.Where(i => i.Assessment.Blocked);
    public long Bytes => Deletable.Sum(i => i.Bytes);
    public int TargetCount => Deletable.Sum(i => i.Targets.Count);
    public bool NeedsConfirmation => Deletable.Any(i => i.Assessment.NeedsConfirmation);
    /// <summary>Drive root → its Recycle Bin (after <see cref="CheckRecycleBins"/>).</summary>
    public Dictionary<string, RecycleBinInfo> Bins { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>What moving to the Recycle Bin will not do as expected (no Recycle Bin, too big for it).</summary>
    public List<string> RecycleNotes { get; } = new();

    /// <summary>
    /// Builds the plan for <paramref name="nodes"/>. <paramref name="contentOlderThan"/>: for folders emptied instead of
    /// deleted, only what did not change for that long goes (default one day: what a program is using now stays).
    /// </summary>
    public static DeletePlan Build(IEnumerable<FsNode> nodes, SafetyContext ctx, TimeSpan? contentOlderThan = null)
    {
        var plan = new DeletePlan();
        var chosen = nodes.Where(n => !n.IsSynthetic).Distinct(ReferenceEqualityComparer.Instance).Cast<FsNode>().ToList();
        // an element inside another chosen one goes with it
        var kept = chosen.Where(n => !chosen.Any(o => !ReferenceEquals(o, n) && o.IsAncestorOf(n))).ToList();
        var age = contentOlderThan ?? TimeSpan.FromDays(1);
        foreach (var n in kept)
        {
            var a = DeleteSafety.Assess(n, ctx);
            var item = new PlanItem { Assessment = a };
            if (!a.Blocked)
            {
                if (a.ContentOnly && n is ContainerNode c)
                {
                    List<FsNode> kids;
                    if (ctx.Sync != null) lock (ctx.Sync) kids = c.Children.Where(k => !k.IsSynthetic).ToList();
                    else kids = c.Children.Where(k => !k.IsSynthetic).ToList();
                    foreach (var k in kids)
                    {
                        if (ctx.NowUtc - NewestWrite(k, ctx.Sync) < age) { item.KeptRecent++; continue; }
                        item.Targets.Add((k, Join(a.Path, k.Name), k.DiskSize));
                    }
                }
                else item.Targets.Add((n, a.Path, n.DiskSize));
                item.Bytes = item.Targets.Sum(t => t.Bytes);
            }
            plan.Items.Add(item);
        }
        return plan;
    }

    internal static string Join(string dir, string name) => dir.EndsWith('\\') ? dir + name : dir + "\\" + name;

    /// <summary>Newest file modification in an element (itself for a file), UTC; default when it holds no files.</summary>
    internal static DateTime NewestWrite(FsNode n, object? sync)
    {
        if (n is FileNode f) return f.Modified;
        DateTime newest = default;
        var stack = new Stack<ContainerNode>();
        stack.Push((ContainerNode)n);
        var kids = new List<FsNode>();
        while (stack.Count > 0)
        {
            var c = stack.Pop();
            kids.Clear();
            if (sync != null) lock (sync) kids.AddRange(c.Children);
            else kids.AddRange(c.Children);
            foreach (var k in kids)
            {
                if (k is FileNode kf) { if (kf.Modified > newest) newest = kf.Modified; }
                else if (k is ContainerNode kc && !kc.IsSynthetic) stack.Push(kc);
            }
        }
        return newest;
    }

    /// <summary>Looks at the Recycle Bin of every drive involved: missing, turned off, or smaller than what goes there.</summary>
    public void CheckRecycleBins()
    {
        Bins.Clear(); RecycleNotes.Clear();
        foreach (var g in Deletable.SelectMany(i => i.Targets).GroupBy(t => Root(t.Path), StringComparer.OrdinalIgnoreCase))
        {
            var bin = RecycleBin.For(g.Key);
            Bins[g.Key] = bin;
            long bytes = g.Sum(t => t.Bytes);
            if (!bin.Available) { RecycleNotes.Add(g.Key + ": " + bin.Note + "."); continue; }
            string cap = bin.MaxIsEstimate ? F($"about {Format.Size(bin.MaxBytes)}") : Format.Size(bin.MaxBytes);
            var big = g.Where(t => t.Bytes > bin.MaxBytes).ToList();
            foreach (var t in big.Take(5))
                RecycleNotes.Add(F($"{t.Path} ({Format.Size(t.Bytes)}) is bigger than the Recycle Bin of {g.Key} ({cap}): Windows will ask whether to delete it permanently."));
            if (big.Count == 0 && bytes + bin.UsedBytes > bin.MaxBytes)
                RecycleNotes.Add(F($"The Recycle Bin of {g.Key} holds {cap} ({Format.Size(bin.UsedBytes)} in it now): to make room Windows removes the oldest deleted files from it for good."));
        }
    }

    private static string Root(string path) => System.IO.Path.GetPathRoot(path) ?? path;

    /// <summary>Tries the rights of everything that would be deleted (up to <paramref name="limitPerItem"/> elements per chosen element).</summary>
    public void Probe(object? sync, CancellationToken token, int limitPerItem = 5000)
    {
        foreach (var item in Deletable)
        {
            var total = new ProbeSummary();
            foreach (var (node, path, _) in item.Targets)
            {
                token.ThrowIfCancellationRequested();
                var s = AccessProbe.ProbeTree(node, path, sync, Math.Max(1, limitPerItem - total.Checked), token);
                total.Checked += s.Checked;
                if (!s.Complete) total.Complete = false;
                foreach (var p in s.NeedsAdmin) total.Add(p, AccessState.NeedsAdmin);
                foreach (var p in s.ProtectedByWindows) total.Add(p, AccessState.ProtectedByWindows);
                foreach (var p in s.InUse) total.Add(p, AccessState.InUse);
                total.NeedsAdminCount += s.NeedsAdminCount - s.NeedsAdmin.Count;
                total.ProtectedCount += s.ProtectedCount - s.ProtectedByWindows.Count;
                total.InUseCount += s.InUseCount - s.InUse.Count;
                if (total.Checked >= limitPerItem) { total.Complete = false; break; }
            }
            item.Probe = total;
        }
    }

    /// <summary>The targets of the deletable items, or of those passing <paramref name="include"/>.</summary>
    public List<(string Path, long Bytes)> TargetsOf(Func<PlanItem, bool>? include = null) =>
        Deletable.Where(i => include?.Invoke(i) ?? true).SelectMany(i => i.Targets).Select(t => (t.Path, t.Bytes)).ToList();
}
