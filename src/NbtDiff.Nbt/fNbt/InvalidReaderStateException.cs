// Vendored from https://github.com/tryashtar/fNbt (BSD-3-Clause, see third_party/NOTICE).
#nullable disable
#pragma warning disable
using System;
using JetBrains.Annotations;

namespace fNbt {
    /// <summary> Exception thrown when an operation is attempted on an NbtReader that
    /// cannot recover from a previous parsing error. </summary>
    [Serializable]
    public sealed class InvalidReaderStateException : InvalidOperationException {
        internal InvalidReaderStateException([NotNull] string message)
            : base(message) {}
    }
}
