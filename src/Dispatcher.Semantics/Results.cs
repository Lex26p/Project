namespace Dispatcher.Semantics;

public sealed record ErrorCode
{
    private ErrorCode(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static ErrorCode From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();

        if (normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
        {
            throw new ArgumentException(
                "An error code may contain only ASCII letters, digits, '.', '_' and '-'.",
                nameof(value));
        }

        return new ErrorCode(normalized);
    }

    public override string ToString() => Value;
}

public sealed record OperationError
{
    public OperationError(ErrorCode code, string message)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Code = code;
        Message = message;
    }

    public ErrorCode Code { get; }

    public string Message { get; }
}

public sealed class Result
{
    private Result(bool isSuccess, OperationError? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public OperationError? Error { get; }

    public static Result Success() => new(true, null);

    public static Result Failure(OperationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result(false, error);
    }

    public static Result<TValue> Success<TValue>(TValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Result<TValue>(true, value, null);
    }

    public static Result<TValue> Failure<TValue>(OperationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new Result<TValue>(false, default, error);
    }
}

public sealed class Result<TValue>
{
    private readonly TValue? value;

    internal Result(bool isSuccess, TValue? value, OperationError? error)
    {
        IsSuccess = isSuccess;
        this.value = value;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public TValue Value => IsSuccess
        ? value!
        : throw new InvalidOperationException("A failed result has no value.");

    public OperationError? Error { get; }

}
