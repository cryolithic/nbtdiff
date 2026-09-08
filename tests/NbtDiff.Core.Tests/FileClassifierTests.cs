namespace NbtDiff.Core.Tests;

public class FileClassifierTests
{
    [Theory]
    [InlineData("region/r.0.0.mca", FileKind.Region)]
    [InlineData("region/r.-1.3.mcr", FileKind.Region)]
    [InlineData("DIM-1/entities/r.0.0.mca", FileKind.Region)]
    [InlineData("region/c.5.-3.mcc", FileKind.Binary)]
    [InlineData("level.dat", FileKind.Nbt)]
    [InlineData("level.dat_old", FileKind.Nbt)]
    [InlineData("playerdata/069a79f4-44e9-4726-a5be-fca90e38aaf5.dat", FileKind.Nbt)]
    [InlineData("data/raids.dat", FileKind.Nbt)]
    [InlineData("data/map_12.dat", FileKind.Nbt)]
    [InlineData("generated/minecraft/structures/house.nbt", FileKind.Nbt)]
    [InlineData("thing.schematic", FileKind.Nbt)]
    [InlineData("thing.litematic", FileKind.Nbt)]
    [InlineData("export.snbt", FileKind.Snbt)]
    [InlineData("stats/069a79f4-44e9-4726-a5be-fca90e38aaf5.json", FileKind.Json)]
    [InlineData("advancements/x.json", FileKind.Json)]
    [InlineData("datapacks/pack/pack.mcmeta", FileKind.Json)]
    [InlineData("session.lock", FileKind.Text)]
    [InlineData("server.properties", FileKind.Text)]
    [InlineData("logs/latest.log", FileKind.Text)]
    [InlineData("serverconfig/forge-server.toml", FileKind.Text)]
    [InlineData("icon.png", FileKind.Binary)]
    [InlineData("noextension", FileKind.Binary)]
    [InlineData("datapacks/pack.zip", FileKind.Binary)]
    [InlineData("LEVEL.DAT", FileKind.Nbt)]
    [InlineData(@"C:\worlds\x\region\r.0.0.MCA", FileKind.Region)]
    public void Classify(string path, FileKind expected)
    {
        Assert.Equal(expected, FileClassifier.Classify(path));
    }
}
