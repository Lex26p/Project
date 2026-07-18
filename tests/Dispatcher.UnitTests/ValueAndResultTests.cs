using System.Reflection;
using Dispatcher.Semantics;
using Xunit;

namespace Dispatcher.UnitTests;

public sealed class ValueAndResultTests
{
    [Fact]
    public void TypedValuesUnitsQualityAndFreshnessRemainExplicit()
    {
        var value = TypedValue.From(12.5);
        var unit = Unit.FromSymbol("°C");

        Assert.Equal(12.5, value.Value);
        Assert.Equal("°C", unit.Symbol);
        Assert.Equal("1", Unit.None.Symbol);
        Assert.Empty(ImplicitConversions(typeof(TypedValue<double>)));
        Assert.NotEqual(typeof(TypedValue<double>), typeof(TypedValue<decimal>));
        Assert.NotEqual(DataQuality.Good, DataQuality.Uncertain);
        Assert.NotEqual(Freshness.Fresh, Freshness.Stale);
    }

    [Fact]
    public void ResultsExposeEitherValueOrError()
    {
        var success = Result.Success(42);
        var error = new OperationError(ErrorCode.From("validation.failed"), "Value is invalid.");
        var failure = Result.Failure<int>(error);

        Assert.True(success.IsSuccess);
        Assert.Equal(42, success.Value);
        Assert.Null(success.Error);
        Assert.True(failure.IsFailure);
        Assert.Same(error, failure.Error);
        Assert.Throws<InvalidOperationException>(() => failure.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("contains space")]
    [InlineData("contains/slash")]
    public void InvalidErrorCodesAreRejected(string code)
    {
        Assert.Throws<ArgumentException>(() => ErrorCode.From(code));
    }

    private static IEnumerable<MethodInfo> ImplicitConversions(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == "op_Implicit");
}
