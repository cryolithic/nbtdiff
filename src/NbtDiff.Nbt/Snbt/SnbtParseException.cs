namespace NbtDiff.Nbt.Snbt;

public sealed class SnbtParseException(string message, int position) : FormatException($"{message} at position {position}")
{
    public int Position { get; } = position;
}
