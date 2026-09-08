using System.Text;

namespace NbtDiff.Nbt;

/// <summary>
/// Why a load did not produce a value. When several strategies were tried (see
/// <see cref="NbtDocument.Load(string)"/>), <see cref="Attempts"/> holds one entry per strategy.
/// </summary>
public sealed record LoadFailure(string Description, Exception? Exception = null)
{
    public IReadOnlyList<LoadFailure> Attempts { get; init; } = [];

    public static LoadFailure Aggregate(string description, IEnumerable<LoadFailure> attempts) =>
        new(description) { Attempts = attempts.ToArray() };

    /// <summary>Description plus the exception message, one line.</summary>
    public string ToShortString() =>
        Exception is null ? Description : $"{Description}: {Exception.Message}";

    /// <summary>Full tree of attempts, one per line, indented.</summary>
    public string ToDetailedString()
    {
        var sb = new StringBuilder();
        Append(sb, this, 0);
        return sb.ToString().TrimEnd();

        static void Append(StringBuilder sb, LoadFailure f, int depth)
        {
            sb.Append(' ', depth * 2).AppendLine(f.ToShortString());
            foreach (var a in f.Attempts)
                Append(sb, a, depth + 1);
        }
    }
}

/// <summary>A value or a failure, never both.</summary>
public readonly record struct LoadResult<T>(T? Value, LoadFailure? Failure) where T : class
{
    public bool Ok => Failure is null;

    public static LoadResult<T> Success(T value) => new(value, null);
    public static LoadResult<T> Fail(LoadFailure failure) => new(null, failure);
    public static LoadResult<T> Fail(string description, Exception? exception = null) =>
        new(null, new LoadFailure(description, exception));

    /// <summary>The value, or throws with the failure description. For tests and callers that already checked <see cref="Ok"/>.</summary>
    public T ValueOrThrow() =>
        Ok ? Value! : throw new InvalidOperationException(Failure!.ToDetailedString(), Failure.Exception);

    public LoadResult<TOut> Map<TOut>(Func<T, TOut> map) where TOut : class =>
        Ok ? LoadResult<TOut>.Success(map(Value!)) : LoadResult<TOut>.Fail(Failure!);

    /// <summary>Runs <paramref name="load"/> and turns any exception into a failure with <paramref name="description"/>.</summary>
    public static LoadResult<T> Try(string description, Func<T> load)
    {
        try
        {
            return Success(load());
        }
        catch (Exception e)
        {
            return Fail(description, e);
        }
    }
}
