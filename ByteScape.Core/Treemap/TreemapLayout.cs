using System.Drawing;
using ByteScape.Core.Model;

namespace ByteScape.Treemap;

/// <summary>One rectangle of the treemap (mirror of a data node for a given view/filter).</summary>
public sealed class TreemapItem
{
    public TreemapItem(FsNode node, TreemapItem? parent, long size, int depth)
    {
        Node = node; Parent = parent; Size = size; Depth = depth;
    }

    public FsNode Node { get; }
    public TreemapItem? Parent { get; }
    public long Size { get; }
    /// <summary>Depth relative to the zoomed root (0).</summary>
    public int Depth { get; }
    public RectangleF Rect { get; set; }
    /// <summary>Area left for the children (below the caption band).</summary>
    public RectangleF Inner { get; set; }
    public List<TreemapItem> Children { get; } = new();
    public bool FolderMaskMatch { get; set; }
    public bool Truncated { get; set; }
    /// <summary>True when the container is expanded: it shows a caption band and its children. Otherwise it is drawn as a plain block.</summary>
    public bool HasCaption { get; set; }

    public bool Contains(PointF p) => Rect.Contains(p);
}
