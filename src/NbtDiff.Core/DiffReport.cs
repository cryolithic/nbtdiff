using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NbtDiff.Core;

public enum ReportFormat { Text, Json }

/// <summary>Flat listing of every row that is not <see cref="RowStatus.Same"/>, plus totals.</summary>
public static class DiffReport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void Write(CompareRoot root, TextWriter writer, ReportFormat format)
    {
        switch (format)
        {
            case ReportFormat.Text: WriteText(root, writer); break;
            case ReportFormat.Json: WriteJson(root, writer); break;
            default: throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    public static string ToString(CompareRoot root, ReportFormat format)
    {
        using var sw = new StringWriter(CultureInfo.InvariantCulture);
        Write(root, sw, format);
        return sw.ToString();
    }

    /// <summary>Rows worth listing: files that are not Same, and directories that exist on one side only or are in error.</summary>
    public static IEnumerable<CompareRow> ReportableRows(CompareRoot root) =>
        root.Root.Descendants().Where(r => r.IsDirectory
            ? r.Status is RowStatus.LeftOnly or RowStatus.RightOnly or RowStatus.Error
            : r.Status != RowStatus.Same);

    private static void WriteText(CompareRoot root, TextWriter w)
    {
        var progress = root.Progress;
        w.WriteLine($"Left:  {root.LeftRoot}");
        w.WriteLine($"Right: {root.RightRoot}");
        w.WriteLine(root.Root.Counts.ToString());
        if (!progress.Completed) w.WriteLine("(scan not complete)");
        else if (progress.Cancelled) w.WriteLine("(scan cancelled)");
        w.WriteLine();

        foreach (var row in ReportableRows(root))
        {
            string path = row.IsDirectory ? row.RelativePath + "/" : row.RelativePath;
            w.Write($"{row.Status,-18} {path}");
            if (!row.IsDirectory)
                w.Write($"  [{Size(row.Left)} | {Size(row.Right)}]");
            else if (row.Counts.Total > 0)
                w.Write($"  ({row.Counts.Total} file{(row.Counts.Total == 1 ? "" : "s")})");
            if (row.Error is not null) w.Write($"  {row.Error}");
            w.WriteLine();
        }
    }

    private static string Size(FileSide? side) => side is null ? "-" : side.Size.ToString(CultureInfo.InvariantCulture);

    private static void WriteJson(CompareRoot root, TextWriter w)
    {
        var progress = root.Progress;
        var dto = new ReportDto(
            root.LeftRoot, root.RightRoot, progress.Completed, progress.Cancelled,
            new CountsDto(root.Root.Counts),
            ReportableRows(root).Select(r => new RowDto(
                r.RelativePath, r.IsDirectory ? "Directory" : r.Kind.ToString(), r.Status, r.Error,
                SideDto.From(r.Left), SideDto.From(r.Right),
                r.IsDirectory ? new CountsDto(r.Counts) : null)).ToList());
        w.Write(JsonSerializer.Serialize(dto, JsonOptions));
        w.WriteLine();
    }

    public sealed record ReportDto(string Left, string Right, bool Completed, bool Cancelled, CountsDto Counts, List<RowDto> Rows);

    public sealed record CountsDto(int Same, int ProbablyDifferent, int Different, int LeftOnly, int RightOnly, int Error, int Pending)
    {
        public CountsDto(RowCounts c) : this(c.Same, c.ProbablyDifferent, c.Different, c.LeftOnly, c.RightOnly, c.Error, c.Pending) { }
    }

    public sealed record RowDto(string Path, string Kind, RowStatus Status, string? Error, SideDto? Left, SideDto? Right, CountsDto? Counts);

    public sealed record SideDto(long Size, DateTime Modified)
    {
        public static SideDto? From(FileSide? s) => s is null ? null : new SideDto(s.Size, s.Modified);
    }
}
