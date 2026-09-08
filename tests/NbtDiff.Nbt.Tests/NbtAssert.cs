using fNbt;
using NbtDiff.TestFixtures;
using Xunit.Sdk;

namespace NbtDiff.Nbt.Tests;

internal static class NbtAssert
{
    public static void Equal(NbtTag? expected, NbtTag? actual)
    {
        if (!NbtEquality.AreEqual(expected, actual, out var difference))
            throw new XunitException($"NBT trees differ {difference}");
    }

    public static T Ok<T>(LoadResult<T> result) where T : class
    {
        if (!result.Ok) throw new XunitException($"Expected success but got failure:\n{result.Failure!.ToDetailedString()}");
        return result.Value!;
    }

    public static LoadFailure Failed<T>(LoadResult<T> result) where T : class
    {
        if (result.Ok) throw new XunitException("Expected failure but load succeeded");
        return result.Failure!;
    }
}
