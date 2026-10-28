using AspirePayments.Api.Endpoints;
using AspirePayments.Data.Domain;
using Xunit;

namespace AspirePayments.Tests.Unit;

/// <summary>
/// A API não pode aceitar um cenário desconhecido silenciosamente: um erro de
/// digitação viraria "Succeed" e esconderia o problema do cliente.
/// </summary>
public class PaymentMappingsTests
{
    [Theory]
    [InlineData("Succeed", PaymentScenario.Succeed)]
    [InlineData("succeed", PaymentScenario.Succeed)]
    [InlineData("FAIL", PaymentScenario.Fail)]
    [InlineData("FailAfterCommit", PaymentScenario.FailAfterCommit)]
    [InlineData("FailTransient", PaymentScenario.FailTransient)]
    public void Known_payment_scenarios_are_parsed_case_insensitively(
        string value,
        PaymentScenario expected)
    {
        var parsed = PaymentMappings.TryParseScenario(value, out var scenario);

        Assert.True(parsed);
        Assert.Equal(expected, scenario);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("3")]
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_or_numeric_payment_scenarios_are_rejected(string? value)
    {
        Assert.False(PaymentMappings.TryParseScenario(value, out _));
    }

    [Theory]
    [InlineData("Succeed", RefundScenario.Succeed)]
    [InlineData("fail", RefundScenario.Fail)]
    public void Known_refund_scenarios_are_parsed(string value, RefundScenario expected)
    {
        var parsed = PaymentMappings.TryParseRefundScenario(value, out var scenario);

        Assert.True(parsed);
        Assert.Equal(expected, scenario);
    }

    [Theory]
    [InlineData("FailTransient")]
    [InlineData("1")]
    [InlineData(null)]
    public void Unknown_refund_scenarios_are_rejected(string? value)
    {
        Assert.False(PaymentMappings.TryParseRefundScenario(value, out _));
    }
}
