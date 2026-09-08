using System.Globalization;
using fNbt;
using NbtDiff.Core;

namespace NbtDiff.App.ViewModels;

/// <summary>One element position of an array pair. A null side means that array is shorter.</summary>
public sealed record ArrayDetailRow(int Index, string? Left, string? Right, bool IsDifferent);

/// <summary>Computes the slice of two arrays shown in the detail pane around their first difference.</summary>
public static class ArrayDetailWindow
{
    public const int DefaultContext = 16;

    /// <summary>
    /// Elements from <c>first − context</c> to <c>first + context</c> (clamped), where <c>first</c> is the
    /// node's first differing index, or 0 for identical or one-sided arrays. Null when neither side is an array.
    /// </summary>
    public static IReadOnlyList<ArrayDetailRow>? Compute(DiffNode node, int context = DefaultContext)
    {
        var left = Elements(node.Left);
        var right = Elements(node.Right);
        if (left is null && right is null) return null;

        int leftLen = left?.Length ?? 0, rightLen = right?.Length ?? 0;
        int maxLen = Math.Max(leftLen, rightLen);
        if (maxLen == 0) return [];

        int center = Math.Clamp(node.Array?.FirstDifference ?? 0, 0, maxLen - 1);
        int start = Math.Max(0, center - context);
        int end = Math.Min(maxLen, center + context + 1);

        var rows = new List<ArrayDetailRow>(end - start);
        for (int i = start; i < end; i++)
        {
            string? l = i < leftLen ? left!.At(i) : null;
            string? r = i < rightLen ? right!.At(i) : null;
            rows.Add(new ArrayDetailRow(i, l, r, !string.Equals(l, r, StringComparison.Ordinal)));
        }
        return rows;
    }

    private sealed record Accessor(int Length, Func<int, string> At);

    private static Accessor? Elements(NbtTag? tag) => tag switch
    {
        NbtByteArray b => new Accessor(b.Value.Length, i => ((sbyte)b.Value[i]).ToString(CultureInfo.InvariantCulture)),
        NbtIntArray n => new Accessor(n.Value.Length, i => n.Value[i].ToString(CultureInfo.InvariantCulture)),
        NbtLongArray l => new Accessor(l.Value.Length, i => l.Value[i].ToString(CultureInfo.InvariantCulture)),
        _ => null,
    };
}
