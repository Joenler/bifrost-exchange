namespace Bifrost.DahAuction.Validation;

/// <summary>
/// Hand-rolled discriminated result type for the <see cref="BidMatrixValidator"/>.
/// Either <see cref="Value"/> is non-default and <see cref="Error"/> is default,
/// or vice versa. <see cref="IsError"/> is the discriminator for callers.
/// </summary>
/// <remarks>
/// First codebase introduction of <c>ValidationResult&lt;T, E&gt;</c>. No
/// existing consumer; future code may adopt this shape or switch to
/// <c>OneOf</c>. Both allocation strategies (OneOf library vs hand-rolled
/// discriminated-union struct) are acceptable.
/// </remarks>
public sealed class ValidationResult<T, E>
    where T : class
    where E : class
{
    public T? Value { get; }
    public E? Error { get; }
    public bool IsError => Error is not null;

    private ValidationResult(T? value, E? error)
    {
        Value = value;
        Error = error;
    }

    public static ValidationResult<T, E> Ok(T value) => new(value, null);
    public static ValidationResult<T, E> Fail(E error) => new(null, error);
}
