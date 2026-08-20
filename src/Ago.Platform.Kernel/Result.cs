namespace Ago.Platform.Kernel;

/// <summary>
/// Outcome of an operation that either completes or fails with a known <see cref="Kernel.Error"/>.
/// For expected failures only - see docs/conventions/coding-style.md.
/// </summary>
public readonly struct Result
{
    private Result(bool isSuccess, Error? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error? Error { get; }

    public static Result Success() => new(true, error: null);

    public static Result Failure(Error error) => new(false, error);

    public static implicit operator Result(Error error) => Failure(error);
}

/// <summary>
/// Outcome of an operation that either produces a <typeparamref name="T"/> or fails with a known
/// <see cref="Kernel.Error"/>.
/// </summary>
public readonly struct Result<T>
{
    private readonly T? _value;

    private Result(bool isSuccess, T? value, Error? error)
    {
        IsSuccess = isSuccess;
        _value = value;
        Error = error;
    }

    public bool IsSuccess { get; }

    public bool IsFailure => !IsSuccess;

    public Error? Error { get; }

    public T Value => IsSuccess
        ? _value! // IsSuccess is only ever true when Success(value) constructed this instance.
        : throw new InvalidOperationException($"Cannot access {nameof(Value)} of a failed Result: {Error}.");

    public static Result<T> Success(T value) => new(true, value, error: null);

    public static Result<T> Failure(Error error) => new(false, default, error);

    public static implicit operator Result<T>(Error error) => Failure(error);

    public static implicit operator Result<T>(T value) => Success(value);
}
