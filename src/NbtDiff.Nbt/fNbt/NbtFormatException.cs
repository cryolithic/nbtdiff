// Vendored from https://github.com/tryashtar/fNbt (BSD-3-Clause, see third_party/NOTICE).
#nullable disable
#pragma warning disable
using System;
using JetBrains.Annotations;

namespace fNbt {
    /// <summary> Exception thrown when a format violation is detected while
    /// parsing or serializing an NBT file. </summary>
    [Serializable]
    public sealed class NbtFormatException : Exception {
        internal NbtFormatException([NotNull] string message)
            : base(message) {}
    }
}
