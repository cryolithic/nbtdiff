// Vendored from https://github.com/tryashtar/fNbt (BSD-3-Clause, see third_party/NOTICE).
#nullable disable
#pragma warning disable
namespace fNbt {
    internal enum NbtParseState {
        AtStreamBeginning,
        AtCompoundBeginning,
        InCompound,
        AtCompoundEnd,
        AtListBeginning,
        InList,
        AtStreamEnd,
        Error
    }
}
