using Ago.Platform.Kernel;

namespace Ago.Platform.Tests;

public class ResultTests
{
    [Fact]
    public void Success_IsSuccessTrue_AndValueIsTheGivenValue()
    {
        var result = Result<int>.Success(42);

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Failure_IsSuccessFalse_AndCarriesTheError()
    {
        var error = new Error("not_found", "Conversation not found.");

        var result = Result<int>.Failure(error);

        Assert.False(result.IsSuccess);
        Assert.True(result.IsFailure);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void Failure_AccessingValue_Throws()
    {
        var result = Result<int>.Failure(new Error("not_found", "Conversation not found."));

        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void ImplicitConversionFromValue_ProducesSuccess()
    {
        Result<int> result = 42;

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void ImplicitConversionFromError_ProducesFailure()
    {
        var error = new Error("capacity_exceeded", "Operator is at capacity.");

        Result<int> result = error;

        Assert.True(result.IsFailure);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void NonGeneric_Success_HasNoError()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
    }

    [Fact]
    public void NonGeneric_Failure_CarriesTheError()
    {
        var error = new Error("forbidden", "Operator does not own this conversation.");

        var result = Result.Failure(error);

        Assert.True(result.IsFailure);
        Assert.Equal(error, result.Error);
    }
}
