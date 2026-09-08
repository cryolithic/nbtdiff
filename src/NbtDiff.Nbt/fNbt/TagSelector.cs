// Vendored from https://github.com/tryashtar/fNbt (BSD-3-Clause, see third_party/NOTICE).
#nullable disable
#pragma warning disable
using JetBrains.Annotations;

namespace fNbt {
    /// <summary> Delegate used to skip loading certain tags of an NBT stream/file. 
    /// The callback should return "true" for any tag that should be read,and "false" for any tag that should be skipped. </summary>
    /// <param name="tag"> Tag that is being read. Tag's type and name are available,
    /// but the value has not yet been read at this time. Guaranteed to never be <c>null</c>. </param>
    public delegate bool TagSelector([NotNull] NbtTag tag);
}
