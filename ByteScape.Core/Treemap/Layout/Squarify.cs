namespace ByteScape.Treemap.Layout;

/// <summary>A run of consecutive items (in layout order) placed side by side in one strip.</summary>
/// <param name="Start">Index of the first item of the row.</param>
/// <param name="Count">Number of items in the row.</param>
/// <param name="Dice">True when the row spans the width (items left to right), false when it spans the height (items top to bottom).</param>
public readonly record struct LayoutRow(int Start, int Count, bool Dice);

/// <summary>
/// Squarified treemap tiling (Bruls, Huizing &amp; van Wijk 2000), formulated exactly like
/// d3-hierarchy's <c>treemapSquarify</c>:
/// <list type="bullet">
/// <item>items are consumed in the given order (callers sort by value descending, see <see cref="LayoutOrder"/>);</item>
/// <item>a row is laid along the <b>shorter</b> side of the remaining rectangle (<c>dice = dx &lt; dy</c>);</item>
/// <item>the row keeps growing while its worst aspect ratio does not get worse
///       (<c>worst = max(maxValue / beta, beta / minValue)</c>, <c>beta = sum² · max(dy/dx, dx/dy) / (remainingValue · ratio)</c>);</item>
/// <item>the row is then positioned with d3's <c>dice</c> / <c>slice</c> and the remaining rectangle shrinks
///       by <c>rowValue / remainingValue</c> of its length.</item>
/// </list>
/// Differences from d3 are limited to robustness: the last row and the last item of each row are
/// flushed to the remaining space (as Apache ECharts does) so floating point drift can never leave
/// gaps or overlaps, and NaN / infinite / negative values are treated as zero.
/// The routine allocates nothing; callers pass spans.
/// </summary>
public static class Squarify
{
    /// <summary>d3's default target aspect ratio (the golden ratio).</summary>
    public static readonly double GoldenRatio = (1 + Math.Sqrt(5)) / 2;

    /// <summary>Sanitizes a value: NaN, infinities and negatives count as zero.</summary>
    public static double Sanitize(double v) => double.IsFinite(v) && v > 0 ? v : 0;

    /// <summary>
    /// Tiles <paramref name="values"/> into <paramref name="bounds"/>. <paramref name="rects"/> receives one
    /// rectangle per value (same index). When <paramref name="rows"/> is given, the row partition is appended
    /// to it so the layout can later be replayed with <see cref="LayoutRows"/> (stable relayout).
    /// </summary>
    /// <param name="ratio">Desired aspect ratio (long side / short side), &gt;= 1. 1 = squares.</param>
    public static void Layout(ReadOnlySpan<double> values, in RectangleD bounds, double ratio, Span<RectangleD> rects, List<LayoutRow>? rows = null)
    {
        int n = values.Length;
        if (n == 0) return;
        if (rects.Length < n) throw new ArgumentException("rects must have one entry per value", nameof(rects));
        if (!(ratio >= 1) || double.IsInfinity(ratio)) ratio = 1;

        double value = 0;
        for (int i = 0; i < n; i++) value += Sanitize(values[i]);

        double x0 = bounds.X, y0 = bounds.Y, x1 = bounds.Right, y1 = bounds.Bottom;
        if (!(value > 0) || !(x1 > x0) || !(y1 > y0) || !bounds.IsFinite)
        {
            // nothing to distribute: every item collapses to an empty rectangle at the origin
            for (int i = 0; i < n; i++) rects[i] = new RectangleD(bounds.X, bounds.Y, 0, 0);
            rows?.Add(new LayoutRow(0, n, true));
            return;
        }

        int i0 = 0, i1 = 0;
        while (i0 < n)
        {
            double dx = x1 - x0, dy = y1 - y0;

            // find the next non-empty item (zeros are swallowed into the row that follows them)
            double sumValue;
            do sumValue = Sanitize(values[i1++]); while (sumValue <= 0 && i1 < n);
            double minValue = sumValue, maxValue = sumValue;

            if (sumValue > 0 && dx > 0 && dy > 0)
            {
                double alpha = Math.Max(dy / dx, dx / dy) / (value * ratio);
                double beta = sumValue * sumValue * alpha;
                double minRatio = Math.Max(maxValue / beta, beta / minValue);

                // keep adding items while the worst aspect ratio of the row maintains or improves
                for (; i1 < n; ++i1)
                {
                    double nodeValue = Sanitize(values[i1]);
                    sumValue += nodeValue;
                    if (nodeValue < minValue) minValue = nodeValue;
                    if (nodeValue > maxValue) maxValue = nodeValue;
                    beta = sumValue * sumValue * alpha;
                    double newRatio = minValue > 0 ? Math.Max(maxValue / beta, beta / minValue) : double.PositiveInfinity;
                    if (newRatio > minRatio) { sumValue -= nodeValue; break; }
                    minRatio = newRatio;
                }
            }
            else
            {
                // remaining items are all empty (or no space is left): put them in one final row
                i1 = n;
                sumValue = 0;
                for (int i = i0; i < n; i++) sumValue += Sanitize(values[i]);
            }

            bool dice = dx < dy;
            bool last = i1 == n;
            rows?.Add(new LayoutRow(i0, i1 - i0, dice));
            if (dice)
            {
                double rowY1 = last || !(value > 0) ? y1 : y0 + dy * sumValue / value;
                Dice(values, i0, i1, sumValue, x0, y0, x1, rowY1, rects);
                y0 = rowY1;
            }
            else
            {
                double rowX1 = last || !(value > 0) ? x1 : x0 + dx * sumValue / value;
                Slice(values, i0, i1, sumValue, x0, y0, rowX1, y1, rects);
                x0 = rowX1;
            }
            value -= sumValue;
            i0 = i1;
        }
    }

    /// <summary>
    /// Re-positions a previously computed row partition with (possibly changed) values — d3's
    /// <c>treemapResquarify</c>. Rows keep their orientation and membership, only their extent moves,
    /// so small value changes produce small rectangle changes.
    /// </summary>
    public static void LayoutRows(ReadOnlySpan<double> values, ReadOnlySpan<LayoutRow> rows, in RectangleD bounds, Span<RectangleD> rects)
    {
        int n = values.Length;
        if (n == 0) return;
        double value = 0;
        for (int i = 0; i < n; i++) value += Sanitize(values[i]);
        double x0 = bounds.X, y0 = bounds.Y, x1 = bounds.Right, y1 = bounds.Bottom;
        if (!(value > 0) || !(x1 > x0) || !(y1 > y0) || !bounds.IsFinite)
        {
            for (int i = 0; i < n; i++) rects[i] = new RectangleD(bounds.X, bounds.Y, 0, 0);
            return;
        }
        for (int r = 0; r < rows.Length; r++)
        {
            var row = rows[r];
            int start = row.Start, end = row.Start + row.Count;
            if (start < 0 || end > n || row.Count <= 0) throw new ArgumentException("row partition does not match the values", nameof(rows));
            double sum = 0;
            for (int i = start; i < end; i++) sum += Sanitize(values[i]);
            bool last = r == rows.Length - 1 || end == n;
            if (row.Dice)
            {
                double rowY1 = last || !(value > 0) ? y1 : y0 + (y1 - y0) * sum / value;
                Dice(values, start, end, sum, x0, y0, x1, rowY1, rects);
                y0 = rowY1;
            }
            else
            {
                double rowX1 = last || !(value > 0) ? x1 : x0 + (x1 - x0) * sum / value;
                Slice(values, start, end, sum, x0, y0, rowX1, y1, rects);
                x0 = rowX1;
            }
            value -= sum;
            if (last) break;
        }
    }

    /// <summary>Items left to right, full row height.</summary>
    private static void Dice(ReadOnlySpan<double> values, int start, int end, double sum, double x0, double y0, double x1, double y1, Span<RectangleD> rects)
    {
        double k = sum > 0 ? (x1 - x0) / sum : 0;
        double x = x0;
        for (int i = start; i < end; i++)
        {
            double xn = i == end - 1 ? x1 : x + Sanitize(values[i]) * k; // flush the last item
            if (xn < x) xn = x;
            rects[i] = new RectangleD(x, y0, xn - x, y1 - y0);
            x = xn;
        }
    }

    /// <summary>Items top to bottom, full row width.</summary>
    private static void Slice(ReadOnlySpan<double> values, int start, int end, double sum, double x0, double y0, double x1, double y1, Span<RectangleD> rects)
    {
        double k = sum > 0 ? (y1 - y0) / sum : 0;
        double y = y0;
        for (int i = start; i < end; i++)
        {
            double yn = i == end - 1 ? y1 : y + Sanitize(values[i]) * k;
            if (yn < y) yn = y;
            rects[i] = new RectangleD(x0, y, x1 - x0, yn - y);
            y = yn;
        }
    }

    /// <summary>Worst (largest) aspect ratio over the non-empty rectangles; 1 when there is none.</summary>
    public static double WorstAspect(ReadOnlySpan<RectangleD> rects)
    {
        double worst = 1;
        foreach (ref readonly var r in rects)
        {
            if (r.Width <= 0 || r.Height <= 0) continue;
            double a = r.AspectRatio;
            if (a > worst) worst = a;
        }
        return worst;
    }
}

/// <summary>Plain slice-and-dice tiling (one strip along a single axis). Used as the "raw" reference in the debug view.</summary>
public static class SliceDice
{
    public static void Layout(ReadOnlySpan<double> values, in RectangleD bounds, bool horizontal, Span<RectangleD> rects)
    {
        int n = values.Length;
        if (n == 0) return;
        double total = 0;
        for (int i = 0; i < n; i++) total += Squarify.Sanitize(values[i]);
        double pos = horizontal ? bounds.X : bounds.Y;
        double end = horizontal ? bounds.Right : bounds.Bottom;
        double k = total > 0 ? (end - pos) / total : 0;
        for (int i = 0; i < n; i++)
        {
            double next = i == n - 1 ? end : pos + Squarify.Sanitize(values[i]) * k;
            if (next < pos) next = pos;
            rects[i] = horizontal
                ? new RectangleD(pos, bounds.Y, next - pos, bounds.Height)
                : new RectangleD(bounds.X, pos, bounds.Width, next - pos);
            pos = next;
        }
    }
}

/// <summary>Deterministic ordering helpers: value descending, ties broken by key, then by original index.</summary>
public static class LayoutOrder
{
    /// <summary>Fills <paramref name="order"/> with indices so that values[order[i]] is descending (stable, deterministic).</summary>
    public static void SortDescending(ReadOnlySpan<double> values, ReadOnlySpan<string> keys, Span<int> order)
    {
        int n = values.Length;
        for (int i = 0; i < n; i++) order[i] = i;
        // insertion sort is fine for small n; for big n use Array.Sort with a comparer over a copied array
        if (n <= 32)
        {
            for (int i = 1; i < n; i++)
            {
                int cur = order[i];
                int j = i - 1;
                while (j >= 0 && Compare(values, keys, order[j], cur) > 0) { order[j + 1] = order[j]; j--; }
                order[j + 1] = cur;
            }
            return;
        }
        var arr = order.Slice(0, n).ToArray();
        var v = values.ToArray();
        var k = keys.Length == n ? keys.ToArray() : null;
        Array.Sort(arr, (a, b) => Compare(v, k, a, b));
        arr.CopyTo(order);
    }

    private static int Compare(ReadOnlySpan<double> values, ReadOnlySpan<string> keys, int a, int b)
    {
        double va = Squarify.Sanitize(values[a]), vb = Squarify.Sanitize(values[b]);
        if (va != vb) return va > vb ? -1 : 1;
        if (keys.Length > 0)
        {
            int c = string.CompareOrdinal(keys[a], keys[b]);
            if (c != 0) return c;
        }
        return a.CompareTo(b);
    }

    private static int Compare(double[] values, string[]? keys, int a, int b)
    {
        double va = Squarify.Sanitize(values[a]), vb = Squarify.Sanitize(values[b]);
        if (va != vb) return va > vb ? -1 : 1;
        if (keys != null)
        {
            int c = string.CompareOrdinal(keys[a], keys[b]);
            if (c != 0) return c;
        }
        return a.CompareTo(b);
    }
}
