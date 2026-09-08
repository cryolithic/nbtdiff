// Vendored from https://github.com/tryashtar/fNbt (BSD-3-Clause, see third_party/NOTICE).
#nullable disable
#pragma warning disable
namespace fNbt {
    // Represents state of a node in the NBT file tree, used by NbtReader
    internal sealed class NbtReaderNode {
        public string ParentName;
        public NbtTagType ParentTagType;
        public NbtTagType ListType;
        public int ParentTagLength;
        public int ListIndex;
    }
}
