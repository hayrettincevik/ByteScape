using System.Globalization;
using System.Text;
using ByteScape.Core.Model;
using ByteScape.Core.Util;

namespace ByteScape.Core.Export;

/// <summary>Values available to a script for one element (tags) plus the running counters.</summary>
public sealed class ScriptContext
{
    public required FsNode Node { get; init; }
    public required ContainerNode ExportRoot { get; init; }
    public required ExportCounters Counters { get; init; }
    public required Func<FsNode, bool> Include { get; init; }
    public DateTime NowUtc { get; init; } = DateTime.UtcNow;
    /// <summary>Disk size as shown in the view (filtered when a filter is active).</summary>
    public Func<FsNode, long> SizeOf { get; init; } = n => n.DiskSize;

    public int NestingLevel
    {
        get
        {
            int l = 0;
            for (FsNode? p = Node; p != null && !ReferenceEquals(p, ExportRoot); p = p.Parent) l++;
            return l;
        }
    }

    public string Tag(string name)
    {
        var n = Node;
        var f = n as FileNode;
        var c = n as ContainerNode;
        switch (name.ToLowerInvariant())
        {
            case "file": return n.Name;
            case "filename": return f?.NameWithoutExtension ?? n.Name;
            case "fileext": return f?.Extension ?? "";
            case "filecreatedate": return f != null ? Format.Date(f.Created) : "";
            case "filecreatetime": return f != null ? Format.Time(f.Created) : "";
            case "filecreateage": return f != null ? Format.Age(f.Created, NowUtc) : "";
            case "filemodifydate": return f != null ? Format.Date(f.Modified) : "";
            case "filemodifytime": return f != null ? Format.Time(f.Modified) : "";
            case "filemodifyage": return f != null ? Format.Age(f.Modified, NowUtc) : "";
            case "fileaccessdate": return f != null ? Format.Date(f.Accessed) : "";
            case "fileaccesstime": return f != null ? Format.Time(f.Accessed) : "";
            case "fileaccessage": return f != null ? Format.Age(f.Accessed, NowUtc) : "";
            case "path": return n.Parent?.FullPath ?? n.FullPath;
            case "pathfile": return n.FullPath;
            case "size": return Format.Size(n.LogicalSize);
            case "sizebytes": return n.LogicalSize.ToString(CultureInfo.InvariantCulture);
            case "disksize": return Format.Size(SizeOf(n));
            case "disksizebytes": return SizeOf(n).ToString(CultureInfo.InvariantCulture);
            case "isfile": return Bool(f != null);
            case "isfolder": return Bool(n.Kind == NodeKind.Folder || (n is DriveNode d && !d.IsDrive));
            case "iscontainer": return Bool(c != null);
            case "count": return Counters.Count.ToString(CultureInfo.InvariantCulture);
            case "localcount": return Counters.LocalCount.ToString(CultureInfo.InvariantCulture);
            case "filecount": return Counters.FileCount.ToString(CultureInfo.InvariantCulture);
            case "localfilecount": return Counters.LocalFileCount.ToString(CultureInfo.InvariantCulture);
            case "foldercount": return Counters.FolderCount.ToString(CultureInfo.InvariantCulture);
            case "localfoldercount": return Counters.LocalFolderCount.ToString(CultureInfo.InvariantCulture);
            case "containercount": return Counters.ContainerCount.ToString(CultureInfo.InvariantCulture);
            case "localcontainercount": return Counters.LocalContainerCount.ToString(CultureInfo.InvariantCulture);
            case "containsfiles": return Bool(c != null && AnyChild(c, x => x is FileNode && Include(x)));
            case "containsfolders": return Bool(c != null && AnyChild(c, x => x is ContainerNode && !x.IsSynthetic && Include(x)));
            case "containscontainers": return Bool(c != null && AnyChild(c, x => x is ContainerNode && !x.IsSynthetic && Include(x)));
            case "nestinglevel": return NestingLevel.ToString(CultureInfo.InvariantCulture);
        }
        return "*BAD TAG*";
    }

    private static string Bool(bool b) => b ? "true" : "false";

    /// <summary>Index based walk: safe against a scan appending children while the export runs outside the tree lock.</summary>
    private static bool AnyChild(ContainerNode c, Func<FsNode, bool> pred)
    {
        for (int i = 0; i < c.ChildCount; i++)
        {
            FsNode x;
            try { x = c.Children[i]; } catch (ArgumentOutOfRangeException) { break; }
            if (pred(x)) return true;
        }
        return false;
    }
}

public sealed class ExportCounters
{
    public int Count, FileCount, FolderCount, ContainerCount;
    public int LocalCount, LocalFileCount, LocalFolderCount, LocalContainerCount;

    public void ResetLocal() { LocalCount = LocalFileCount = LocalFolderCount = LocalContainerCount = 0; }

    /// <summary>Counters increment after each element evaluation ("starts from 0").</summary>
    public void Advance(FsNode n)
    {
        Count++; LocalCount++;
        if (n is FileNode) { FileCount++; LocalFileCount++; }
        if (n is ContainerNode) { FolderCount++; LocalFolderCount++; ContainerCount++; LocalContainerCount++; }
    }
}

/// <summary>
/// The export mini scripting language:
///   text, &lt;%tag%&gt;, {&amp;br} {&amp;tab} {&amp;curlyop} {&amp;curlycl} {&amp;angleop} {&amp;anglecl},
///   {leftpad{text}{pad}{len}}, {rightpad{text}{pad}{len}}, {nest{text}},
///   {if{a}op{b}?{then}[:{else}]} with op in == != &lt; &lt;= &gt; &gt;=,
///   {script} at the start switches to script mode (literal tabs/newlines are ignored).
/// The template is parsed once into a tree and evaluated per element.
/// </summary>
public sealed class Script
{
    private readonly List<Node> _nodes;
    public bool ScriptMode { get; }

    private abstract class Node { }
    private sealed class Text : Node { public string Value = ""; }
    private sealed class Tag : Node { public string Name = ""; }
    private sealed class Command : Node { public string Name = ""; public List<List<Node>> Args = new(); public string Op = ""; public List<Node>? Else; }

    public Script(string template)
    {
        template ??= string.Empty;
        var t = template.TrimStart();
        ScriptMode = t.StartsWith("{script}", StringComparison.OrdinalIgnoreCase);
        if (ScriptMode) template = t.Substring("{script}".Length);
        int pos = 0;
        _nodes = ParseSequence(template, ref pos, stopAtClose: false);
    }

    // ------------------------------------------------------------------ parser

    private List<Node> ParseSequence(string s, ref int pos, bool stopAtClose)
    {
        var list = new List<Node>();
        var text = new StringBuilder();
        void Flush() { if (text.Length > 0) { list.Add(new Text { Value = text.ToString() }); text.Clear(); } }
        while (pos < s.Length)
        {
            char ch = s[pos];
            if (ch == '}' && stopAtClose) { Flush(); return list; }
            if (ch == '<' && pos + 1 < s.Length && s[pos + 1] == '%')
            {
                int end = s.IndexOf("%>", pos + 2, StringComparison.Ordinal);
                if (end > 0)
                {
                    Flush();
                    list.Add(new Tag { Name = s.Substring(pos + 2, end - pos - 2).Trim() });
                    pos = end + 2;
                    continue;
                }
            }
            if (ch == '{')
            {
                Flush();
                pos++;
                list.AddRange(ParseBlock(s, ref pos));
                continue;
            }
            if (ScriptMode && (ch == '\r' || ch == '\n' || ch == '\t')) { pos++; continue; }
            text.Append(ch);
            pos++;
        }
        Flush();
        return list;
    }

    private static readonly HashSet<string> CommandNames = new(StringComparer.OrdinalIgnoreCase) { "if", "leftpad", "rightpad", "nest", "script" };

    /// <summary>
    /// A "{...}" block is either a command ("{if ...}", "{leftpad ...}", "{&amp;br}") or a literal
    /// sequence ("{true}", "{  [text] <%file%>}"). The first word decides, so the braces of an
    /// argument can double as the braces of a nested command ("? { if {a} == {b} ? {x} }").
    /// Called with <paramref name="pos"/> just after the opening brace; consumes the closing one.
    /// </summary>
    private List<Node> ParseBlock(string s, ref int pos)
    {
        int start = pos;
        SkipWs(s, ref pos);
        var name = new StringBuilder();
        while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '&' || s[pos] == '_')) name.Append(s[pos++]);
        string ident = name.ToString();
        if (ident.StartsWith('&') || CommandNames.Contains(ident))
            return new List<Node> { ParseCommand(ident.ToLowerInvariant(), s, ref pos) };
        pos = start;
        var seq = ParseSequence(s, ref pos, stopAtClose: true);
        if (pos < s.Length && s[pos] == '}') pos++;
        return seq;
    }

    /// <summary>Parses the arguments of a command whose name has already been read.</summary>
    private Command ParseCommand(string name, string s, ref int pos)
    {
        var cmd = new Command { Name = name };
        bool pendingElse = false;
        while (true)
        {
            SkipWs(s, ref pos);
            if (pos >= s.Length) break;
            char ch = s[pos];
            if (ch == '}') { pos++; break; }
            if (ch == '{')
            {
                pos++;
                var arg = ParseBlock(s, ref pos);
                if (cmd.Name == "if" && pendingElse) { cmd.Else = arg; pendingElse = false; }
                else cmd.Args.Add(arg);
                continue;
            }
            if (cmd.Name == "if")
            {
                if (ch == ':') { pendingElse = true; pos++; continue; }
                if (ch == '?') { pos++; continue; }
                var op = new StringBuilder();
                while (pos < s.Length && "=!<>".IndexOf(s[pos]) >= 0) op.Append(s[pos++]);
                if (op.Length > 0) { cmd.Op = op.ToString(); continue; }
            }
            // unexpected char inside a command: skip it to be forgiving
            pos++;
        }
        return cmd;
    }

    private static void SkipWs(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos])) pos++;
    }

    // ------------------------------------------------------------------ evaluation

    public string Evaluate(ScriptContext ctx)
    {
        var sb = new StringBuilder();
        Eval(_nodes, ctx, sb);
        return sb.ToString();
    }

    private static void Eval(List<Node> nodes, ScriptContext ctx, StringBuilder sb)
    {
        foreach (var n in nodes)
        {
            switch (n)
            {
                case Text t: sb.Append(t.Value); break;
                case Tag tag: sb.Append(ctx.Tag(tag.Name)); break;
                case Command c: EvalCommand(c, ctx, sb); break;
            }
        }
    }

    private static string EvalToString(List<Node> nodes, ScriptContext ctx)
    {
        var sb = new StringBuilder();
        Eval(nodes, ctx, sb);
        return sb.ToString();
    }

    private static void EvalCommand(Command c, ScriptContext ctx, StringBuilder sb)
    {
        switch (c.Name)
        {
            case "&br": sb.Append('\n'); return; // a bare LF, whatever the platform
            case "&tab": sb.Append('\t'); return;
            case "&curlyop": sb.Append('{'); return;
            case "&curlycl": sb.Append('}'); return;
            case "&angleop": sb.Append('<'); return;
            case "&anglecl": sb.Append('>'); return;
            case "script": return;
            case "leftpad":
            case "rightpad":
            {
                if (c.Args.Count < 3) { sb.Append("*BAD PAD*"); return; }
                string text = EvalToString(c.Args[0], ctx);
                string pad = EvalToString(c.Args[1], ctx);
                if (!int.TryParse(EvalToString(c.Args[2], ctx).Trim(), out int len) || pad.Length == 0) { sb.Append("*BAD PAD*"); return; }
                if (text.Length >= len) { sb.Append(c.Name == "leftpad" ? text.Substring(text.Length - len) : text.Substring(0, len)); return; }
                var padding = new StringBuilder();
                while (padding.Length + text.Length < len) padding.Append(pad);
                string p = padding.ToString().Substring(0, len - text.Length);
                sb.Append(c.Name == "leftpad" ? p + text : text + p);
                return;
            }
            case "nest":
            {
                if (c.Args.Count < 1) { sb.Append("*VOID*"); return; }
                string text = EvalToString(c.Args[0], ctx);
                int n = ctx.NestingLevel;
                for (int i = 0; i < n; i++) sb.Append(text);
                return;
            }
            case "if":
            {
                if (c.Args.Count < 3) { sb.Append("*VOID*"); return; }
                string a = EvalToString(c.Args[0], ctx), b = EvalToString(c.Args[1], ctx);
                bool res = Compare(a, b, c.Op);
                if (res) Eval(c.Args[2], ctx, sb);
                else if (c.Else != null) Eval(c.Else, ctx, sb);
                else if (c.Args.Count > 3) Eval(c.Args[3], ctx, sb);
                return;
            }
        }
        sb.Append("*VOID*");
    }

    private static bool Compare(string a, string b, string op)
    {
        int cmp;
        if (double.TryParse(a.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var da) &&
            double.TryParse(b.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var db))
            cmp = da.CompareTo(db);
        else
            cmp = string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        return op switch
        {
            "==" or "=" => cmp == 0,
            "!=" or "<>" => cmp != 0,
            "<" => cmp < 0,
            "<=" => cmp <= 0,
            ">" => cmp > 0,
            ">=" => cmp >= 0,
            _ => false,
        };
    }
}
