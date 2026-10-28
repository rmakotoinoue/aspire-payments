using AspirePayments.Data.Domain;
using AspirePayments.Data.Payments;
using Xunit;

namespace AspirePayments.Tests.Unit;

/// <summary>
/// Testes unitários do gateway simulado. Cobrem a determinabilidade dos
/// cenários sem tocar em banco ou broker.
/// </summary>
public class SimulatedPaymentGatewayTests
{
    private static SimulatedPaymentGateway CreateGateway(int latencyMilliseconds = 0)
        => new(new PaymentGatewayOptions { SimulatedLatencyMilliseconds = latencyMilliseconds });

    [Fact]
    public async Task Succeed_approves_the_charge()
    {
        var result = await CreateGateway().ChargeAsync(
            PaymentScenario.Succeed, Guid.NewGuid(), 100m, "BRL", CancellationToken.None);

        Assert.True(result.Approved);
        Assert.Null(result.FailureReason);
        Assert.NotEmpty(result.GatewayReference);
    }

    [Fact]
    public async Task Fail_declines_the_charge_with_a_reason()
    {
        var result = await CreateGateway().ChargeAsync(
            PaymentScenario.Fail, Guid.NewGuid(), 100m, "BRL", CancellationToken.None);

        Assert.False(result.Approved);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task FailAfterCommit_approves_the_charge()
    {
        // O gateway aprova; a falha acontece depois do commit, no worker.
        var result = await CreateGateway().ChargeAsync(
            PaymentScenario.FailAfterCommit, Guid.NewGuid(), 100m, "BRL", CancellationToken.None);

        Assert.True(result.Approved);
    }

    [Fact]
    public async Task FailTransient_throws_a_transient_exception()
    {
        await Assert.ThrowsAsync<TransientPaymentException>(() => CreateGateway().ChargeAsync(
            PaymentScenario.FailTransient, Guid.NewGuid(), 100m, "BRL", CancellationToken.None));
    }

    [Fact]
    public async Task Refund_Succeed_returns_money()
    {
        var result = await CreateGateway().RefundAsync(
            RefundScenario.Succeed, Guid.NewGuid(), 100m, "BRL", CancellationToken.None);

        Assert.True(result.Approved);
    }

    [Fact]
    public async Task Refund_Fail_declines_the_refund()
    {
        var result = await CreateGateway().RefundAsync(
            RefundScenario.Fail, Guid.NewGuid(), 100m, "BRL", CancellationToken.None);

        Assert.False(result.Approved);
        Assert.NotNull(result.FailureReason);
    }
}
