using fNbt;
using NbtDiff.Nbt;

namespace NbtDiff.TestFixtures;

/// <summary>
/// Builds small Java Edition worlds with deterministic content. Every transformation returns a new
/// builder with a deep copy of the model, so a base world can be written in several variants:
/// <code>
/// var w = new WorldBuilder(seed: 42).WithRegion(0, 0, chunks: 40).WithLevelDat();
/// w.Write(left);
/// w.Mutate(m => m.Chunk(0, 0, 3, 1).SetPath("InhabitedTime", 999L)).Write(right);
/// </code>
/// </summary>
public sealed class WorldBuilder
{
    private const int ChunksPerAxis = RegionCoords.ChunksPerAxis;

    private readonly int _seed;
    private readonly SortedDictionary<(int rx, int rz), SortedDictionary<(int x, int z), NbtCompound>> _regions;
    private readonly SortedDictionary<string, NbtCompound> _files;   // relative path → root, written as Java GZip NBT
    private readonly byte _scheme;
    private readonly uint _timestampBase;
    private readonly RegionWriteOptions _regionOptions;

    public WorldBuilder(int seed = 42)
        : this(seed, [], [], ChunkRef.SchemeZLib, 1_600_000_000, new RegionWriteOptions()) { }

    private WorldBuilder(int seed,
        SortedDictionary<(int, int), SortedDictionary<(int, int), NbtCompound>> regions,
        SortedDictionary<string, NbtCompound> files,
        byte scheme, uint timestampBase, RegionWriteOptions regionOptions)
    {
        _seed = seed;
        _regions = regions;
        _files = files;
        _scheme = scheme;
        _timestampBase = timestampBase;
        _regionOptions = regionOptions;
    }

    private WorldBuilder Clone(byte? scheme = null, uint? timestampBase = null, RegionWriteOptions? regionOptions = null)
    {
        var regions = new SortedDictionary<(int, int), SortedDictionary<(int, int), NbtCompound>>();
        foreach (var (key, chunks) in _regions)
            regions[key] = new SortedDictionary<(int, int), NbtCompound>(chunks.ToDictionary(c => c.Key, c => (NbtCompound)c.Value.Clone()));
        var files = new SortedDictionary<string, NbtCompound>(_files.ToDictionary(f => f.Key, f => (NbtCompound)f.Value.Clone()));
        return new WorldBuilder(_seed, regions, files, scheme ?? _scheme, timestampBase ?? _timestampBase, regionOptions ?? _regionOptions);
    }

    /// <summary>Adds a region with the first <paramref name="chunks"/> slots in (z, x) order filled.</summary>
    public WorldBuilder WithRegion(int rx, int rz, int chunks)
    {
        if (chunks is < 0 or > ChunksPerAxis * ChunksPerAxis) throw new ArgumentOutOfRangeException(nameof(chunks));
        var w = Clone();
        var region = w._regions.TryGetValue((rx, rz), out var r) ? r : w._regions[(rx, rz)] = [];
        var coords = new RegionCoords(rx, rz);
        for (int i = 0; i < chunks; i++)
        {
            int x = i % ChunksPerAxis, z = i / ChunksPerAxis;
            var (wx, wz) = coords.ChunkAt(x, z);
            region[(x, z)] = MakeChunk(_seed, wx, wz);
        }
        return w;
    }

    public WorldBuilder WithLevelDat(string levelName = "Fixture World")
    {
        var w = Clone();
        var rng = new Random(_seed);
        w._files["level.dat"] = new NbtCompound("")
        {
            new NbtCompound("Data")
            {
                new NbtString("LevelName", levelName),
                new NbtInt("DataVersion", 3465),
                new NbtLong("Time", rng.Next(0, 1_000_000)),
                new NbtLong("DayTime", rng.Next(0, 24_000)),
                new NbtInt("SpawnX", rng.Next(-100, 100)),
                new NbtInt("SpawnY", 64),
                new NbtInt("SpawnZ", rng.Next(-100, 100)),
                new NbtByte("hardcore", 0),
                new NbtCompound("Version") { new NbtString("Name", "1.20.1"), new NbtInt("Id", 3465), new NbtByte("Snapshot", 0) },
                new NbtCompound("WorldGenSettings") { new NbtLong("seed", _seed) },
            },
        };
        return w;
    }

    public WorldBuilder WithPlayer(Guid id)
    {
        var w = Clone();
        var rng = new Random(StableHash(_seed, BitConverter.ToInt32(id.ToByteArray(), 0), 0));
        var uuid = id.ToByteArray();
        w._files[$"playerdata/{id}.dat"] = new NbtCompound("")
        {
            new NbtList("Pos", NbtTagType.Double) { new NbtDouble(rng.NextDouble() * 100), new NbtDouble(64), new NbtDouble(rng.NextDouble() * 100) },
            new NbtFloat("Health", 20f),
            new NbtInt("foodLevel", 20),
            new NbtIntArray("UUID", [BitConverter.ToInt32(uuid, 0), BitConverter.ToInt32(uuid, 4), BitConverter.ToInt32(uuid, 8), BitConverter.ToInt32(uuid, 12)]),
            new NbtList("Inventory", NbtTagType.Compound)
            {
                new NbtCompound { new NbtByte("Slot", 0), new NbtString("id", "minecraft:diamond_pickaxe"), new NbtByte("Count", 1) },
            },
            new NbtString("Dimension", "minecraft:overworld"),
        };
        return w;
    }

    /// <summary>Adds an arbitrary Java NBT file at a relative path (forward slashes).</summary>
    public WorldBuilder WithFile(string relativePath, NbtCompound root)
    {
        var w = Clone();
        w._files[relativePath] = (NbtCompound)root.Clone();
        return w;
    }

    public WorldBuilder Mutate(Action<WorldMutator> mutate)
    {
        var w = Clone();
        mutate(new WorldMutator(w));
        return w;
    }

    /// <summary>Same content, every chunk stored with a different compression scheme.</summary>
    public WorldBuilder Recompress(byte scheme) => Clone(scheme: scheme);

    /// <summary>Same content, every chunk's header timestamp bumped.</summary>
    public WorldBuilder TouchTimestamps() => Clone(timestampBase: _timestampBase + 1000);

    /// <summary>Same content, chunks stored in reverse sector order.</summary>
    public WorldBuilder Defragment() => Clone(regionOptions: _regionOptions with { Order = SectorOrder.Reverse });

    /// <summary>Same content, empty sectors between chunks.</summary>
    public WorldBuilder Fragment(int gapSectors = 1) => Clone(regionOptions: _regionOptions with { GapSectors = gapSectors });

    public IEnumerable<(int rx, int rz)> Regions => _regions.Keys;
    public IEnumerable<(int x, int z)> ChunksIn(int rx, int rz) => _regions[(rx, rz)].Keys;
    public NbtCompound Chunk(int rx, int rz, int x, int z) => _regions[(rx, rz)][(x, z)];
    public NbtCompound File(string relativePath) => _files[relativePath];
    public IEnumerable<string> Files => _files.Keys;

    /// <summary>Writes the world into <paramref name="directory"/>, creating it. Existing files are overwritten.</summary>
    public void Write(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var (path, root) in _files)
            NbtFixtures.WriteFile(Path.Combine(directory, path), root, NbtFormat.JavaNbt, NbtCompression.GZip);

        foreach (var ((rx, rz), chunks) in _regions)
        {
            var specs = chunks.Select(c => new ChunkSpec(c.Key.x, c.Key.z, c.Value, _scheme, Timestamp: _timestampBase + (uint)(c.Key.z * ChunksPerAxis + c.Key.x)));
            RegionWriter.Write(Path.Combine(directory, "region", $"r.{rx}.{rz}.mca"), specs, _regionOptions);
        }
        System.IO.File.WriteAllText(Path.Combine(directory, "session.lock"), "☃");
    }

    /// <summary>A modern (1.18+) chunk shape, content derived from seed and position only.</summary>
    public static NbtCompound MakeChunk(int seed, int cx, int cz)
    {
        // Not HashCode.Combine: that is randomized per process, which would make fixtures differ between runs.
        var rng = new Random(StableHash(seed, cx, cz));
        var chunk = new NbtCompound("")
        {
            new NbtInt("DataVersion", 3465),
            new NbtInt("xPos", cx),
            new NbtInt("yPos", -4),
            new NbtInt("zPos", cz),
            new NbtString("Status", "minecraft:full"),
            new NbtLong("LastUpdate", rng.NextInt64(0, 10_000_000)),
            new NbtLong("InhabitedTime", rng.Next(0, 100_000)),
            new NbtList("sections", NbtTagType.Compound)
            {
                new NbtCompound
                {
                    new NbtByte("Y", 0),
                    new NbtCompound("block_states")
                    {
                        new NbtList("palette", NbtTagType.Compound)
                        {
                            new NbtCompound { new NbtString("Name", "minecraft:stone") },
                            new NbtCompound { new NbtString("Name", "minecraft:dirt") },
                            new NbtCompound { new NbtString("Name", "minecraft:grass_block"), new NbtCompound("Properties") { new NbtString("snowy", "false") } },
                        },
                        new NbtLongArray("data", Enumerable.Range(0, 256).Select(_ => rng.NextInt64()).ToArray()),
                    },
                },
            },
            new NbtList("block_entities", NbtTagType.Compound),
            new NbtCompound("Heightmaps")
            {
                new NbtLongArray("MOTION_BLOCKING", Enumerable.Range(0, 37).Select(_ => rng.NextInt64()).ToArray()),
            },
        };
        return chunk;
    }

    private static int StableHash(int a, int b, int c)
    {
        unchecked
        {
            int h = (int)2166136261;
            foreach (int v in (ReadOnlySpan<int>)[a, b, c])
                h = (h ^ v) * 16777619;
            return h;
        }
    }

    public sealed class WorldMutator
    {
        private readonly WorldBuilder _w;
        internal WorldMutator(WorldBuilder w) => _w = w;

        public NbtCompound Chunk(int rx, int rz, int x, int z) => _w._regions[(rx, rz)][(x, z)];
        public NbtCompound File(string relativePath) => _w._files[relativePath];

        public WorldMutator RemoveChunk(int rx, int rz, int x, int z)
        {
            _w._regions[(rx, rz)].Remove((x, z));
            return this;
        }

        public WorldMutator AddChunk(int rx, int rz, int x, int z)
        {
            var (wx, wz) = new RegionCoords(rx, rz).ChunkAt(x, z);
            _w._regions[(rx, rz)][(x, z)] = MakeChunk(_w._seed, wx, wz);
            return this;
        }

        public WorldMutator RemoveFile(string relativePath)
        {
            _w._files.Remove(relativePath);
            return this;
        }
    }
}

/// <summary>Slash-separated paths into compounds, e.g. <c>Data/Version/Name</c>.</summary>
public static class NbtPathExtensions
{
    public static NbtTag? GetPath(this NbtCompound root, string path)
    {
        NbtTag? current = root;
        foreach (var segment in path.Split('/'))
        {
            if (current is not NbtCompound c || !c.TryGet(segment, out NbtTag next)) return null;
            current = next;
        }
        return current;
    }

    /// <summary>Replaces (or adds) the tag at <paramref name="path"/>; intermediate compounds must exist. Returns the root for chaining.</summary>
    public static NbtCompound SetPath(this NbtCompound root, string path, NbtTag value)
    {
        int slash = path.LastIndexOf('/');
        var parent = slash < 0 ? root : root.GetPath(path[..slash]) as NbtCompound
            ?? throw new ArgumentException($"No compound at '{path[..slash]}'");
        string name = path[(slash + 1)..];
        parent.Remove(name);
        value.Name = name;
        parent.Add(value);
        return root;
    }

    public static NbtCompound SetPath(this NbtCompound root, string path, long value) => root.SetPath(path, new NbtLong(value));
    public static NbtCompound SetPath(this NbtCompound root, string path, int value) => root.SetPath(path, new NbtInt(value));
    public static NbtCompound SetPath(this NbtCompound root, string path, string value) => root.SetPath(path, new NbtString(value));
}
