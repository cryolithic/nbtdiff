// Vendored from https://github.com/tryashtar/fNbt (BSD-3-Clause, see third_party/NOTICE).
#nullable disable
#pragma warning disable
namespace fNbt {
    // Represents state of a node in the NBT file tree, used by NbtWriter
    internal sealed class NbtWriterNode {
        public NbtTagType ParentType;
        public NbtTagType ListType;
        public int ListSize;
        public int ListIndex;
    }
}
