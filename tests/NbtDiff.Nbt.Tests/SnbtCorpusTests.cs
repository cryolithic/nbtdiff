using NbtDiff.Nbt.Snbt;

namespace NbtDiff.Nbt.Tests;

/// <summary>
/// Parses every <c>.snbt</c> under the directory named by <c>NBTDIFF_SNBT_CORPUS</c> (recursively) —
/// e.g. a modpack's <c>config/ftbquests</c>, which is written in FTB's comma-less dialect. Skipped
/// when the variable is unset.
/// </summary>
public class SnbtCorpusTests
{
    [Fact]
    public void EveryFileInCorpus_Parses()
    {
        var dir = Environment.GetEnvironmentVariable("NBTDIFF_SNBT_CORPUS");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        var files = Directory.EnumerateFiles(dir, "*.snbt", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);
        var failures = new List<string>();
        foreach (var file in files)
        {
            var doc = NbtDocument.Load(file);
            if (!doc.Ok)
            {
                var snbt = doc.Failure!.Attempts.FirstOrDefault(a => a.Description.Contains("SNBT")) ?? doc.Failure;
                failures.Add($"{Path.GetRelativePath(dir, file)}: {snbt.ToShortString()}");
                continue;
            }
            // And the writer must produce text the parser reads back to the same tree.
            var again = SnbtParser.Parse(SnbtWriter.Write(doc.Value!.Root, SnbtOptions.Pretty));
            again.Name = "";
            if (!TestFixtures.NbtEquality.AreEqual(doc.Value.Root, again, out var diff))
                failures.Add($"{Path.GetRelativePath(dir, file)}: round trip differs {diff}");
        }
        Assert.True(failures.Count == 0, $"{failures.Count} of {files.Count} files failed:\n" + string.Join("\n", failures.Take(25)));
    }
}
