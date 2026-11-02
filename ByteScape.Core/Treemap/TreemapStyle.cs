using System.Drawing;
using ByteScape.Core.Config;
using ByteScape.Core.Model;

namespace ByteScape.Treemap;

/// <summary>
/// Colour rules shared by the treemap control and the configuration preview, so what the
/// preview shows is exactly what the views paint.
/// </summary>
public static class TreemapStyle
{
    public static Color BaseColor(NodeKind kind, AppConfig cfg) => kind switch
    {
        NodeKind.Drive => cfg.DriveColor,
        NodeKind.Folder => cfg.FolderColor,
        NodeKind.FreeSpace => cfg.FreeColor,
        NodeKind.UnknownSpace => cfg.UnknownColor,
        NodeKind.MissingData => Color.LightGray,
        NodeKind.Rest => Blend(cfg.FolderColor, cfg.FileColor, 0.5f),
        _ => cfg.FileColor,
    };

    /// <summary>Each nesting level is darkened by a constant ratio (8% at the default contrast of 40).</summary>
    public static Color ShadeForLevel(Color c, int depth, AppConfig cfg)
    {
        float perLevel = 0.2f * cfg.LevelContrast / 100f;
        float factor = 1f - (float)Math.Pow(1 - perLevel, depth);
        return Blend(c, Color.Black, Math.Min(0.8f, factor));
    }

    public static Color BorderColor(Color fill, AppConfig cfg) => Blend(fill, Color.Black, 0.12f + 0.5f * cfg.BorderContrast / 100f);
    public static Color LightColor(Color fill, AppConfig cfg) => Blend(fill, Color.White, 0.25f + 0.3f * cfg.BorderContrast / 100f);

    public static Color TagBrush(TagColor tag, AppConfig cfg) => tag switch
    {
        TagColor.Red => cfg.TagRedColor,
        TagColor.Yellow => cfg.TagYellowColor,
        TagColor.Green => cfg.TagGreenColor,
        TagColor.Blue => cfg.TagBlueColor,
        _ => Color.Gray,
    };

    public static Color TextColor(Color fill) => Luma(fill) < 110 ? Color.White : Color.Black;

    public static Color Blend(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    public static int Luma(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
}
