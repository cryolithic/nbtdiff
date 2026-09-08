// Vendored from https://github.com/tryashtar/fNbt (BSD-3-Clause, see third_party/NOTICE).
#nullable disable
#pragma warning disable
namespace fNbt {
    /// <summary> nbt-diff: vestigial from the fork's undo system. Only exists so the
    /// PerformAction call sites in the tag setters compile; it is never read. </summary>
    public sealed class DescriptionHolder {
        public DescriptionHolder(string format, params object[] objects) {}
    }
}
