using TransactionService.Domain.Entities;
using TransactionService.Domain.Enums;
using Xunit;

namespace TransactionService.Tests;

// ── Transaction aggregate domain tests ────────────────────────────────────────

public class TransactionDomainTests
{
    // ── Factory methods ───────────────────────────────────────────────────────

    [Fact]
    public void CreateDeposit_SetsCorrectDefaults()
    {
        var t = Transaction.CreateDeposit(
            Guid.NewGuid(), 5000m, "PAY_REF_001", "key-1");

        Assert.Equal(TransactionType.Deposit, t.Type);
        Assert.Equal(TransactionStatus.Pending, t.Status);
        Assert.Equal(5000m, t.Amount);
        Assert.Equal(DebitSource.None, t.DebitSource);
        Assert.Equal("PAY_REF_001", t.PaystackReference);
        Assert.Equal("NGN", t.Currency);
        Assert.Null(t.CounterpartyUserId);
    }

    [Fact]
    public void CreateInPlatformPayment_SetsCorrectDefaults()
    {
        var payerId    = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        var t = Transaction.CreateInPlatformPayment(payerId, recipientId, 1500m, "key-2");

        Assert.Equal(TransactionType.InPlatformPayment, t.Type);
        Assert.Equal(TransactionStatus.Pending, t.Status);
        Assert.Equal(payerId, t.UserId);
        Assert.Equal(recipientId, t.CounterpartyUserId);
        Assert.Equal(DebitSource.None, t.DebitSource);
    }

    [Fact]
    public void CreateExternalTransfer_SetsMainAsDebitSource()
    {
        var t = Transaction.CreateExternalTransfer(
            Guid.NewGuid(), 10000m, "transfer-id-abc", "key-3");

        Assert.Equal(TransactionType.ExternalTransfer, t.Type);
        Assert.Equal(DebitSource.Main, t.DebitSource);  // External transfers always use Main
        Assert.Equal("transfer-id-abc", t.ExternalTransferId);
    }

    [Fact]
    public void CreateAny_ZeroAmount_Throws()
    {
        Assert.Throws<TransactionDomainException>(
            () => Transaction.CreateDeposit(Guid.NewGuid(), 0m, "ref", "key"));
        Assert.Throws<TransactionDomainException>(
            () => Transaction.CreateInPlatformPayment(Guid.NewGuid(), Guid.NewGuid(), -1m, "key"));
        Assert.Throws<TransactionDomainException>(
            () => Transaction.CreateExternalTransfer(Guid.NewGuid(), 0m, "ref", "key"));
    }

    // ── State: Pending → Processing ───────────────────────────────────────────

    [Fact]
    public void RecordDebitAndMarkProcessing_FromPending_Budget_SetsState()
    {
        var t = Transaction.CreateInPlatformPayment(
            Guid.NewGuid(), Guid.NewGuid(), 500m, "key-4");

        t.RecordDebitAndMarkProcessing(DebitSource.Budget, 500m, 0m);

        Assert.Equal(TransactionStatus.Processing, t.Status);
        Assert.Equal(DebitSource.Budget, t.DebitSource);
        Assert.Equal(500m, t.BudgetDebited);
        Assert.Null(t.MainDebited);
        Assert.NotNull(t.ProcessingStartedAt);
    }

    [Fact]
    public void RecordDebitAndMarkProcessing_FromPending_Main_SetsState()
    {
        var t = Transaction.CreateInPlatformPayment(
            Guid.NewGuid(), Guid.NewGuid(), 300m, "key-5");

        t.RecordDebitAndMarkProcessing(DebitSource.Main, 0m, 300m);

        Assert.Equal(DebitSource.Main, t.DebitSource);
        Assert.Null(t.BudgetDebited);
        Assert.Equal(300m, t.MainDebited);
    }

    [Fact]
    public void RecordDebitAndMarkProcessing_FromProcessing_IsIdempotentUpdate()
    {
        // When both budget and main are used, two wallet events arrive sequentially
        var t = Transaction.CreateInPlatformPayment(
            Guid.NewGuid(), Guid.NewGuid(), 500m, "key-6");

        t.RecordDebitAndMarkProcessing(DebitSource.Budget, 300m, 0m); // budget event
        t.RecordDebitAndMarkProcessing(DebitSource.Main, 300m, 200m);  // main fallback event

        Assert.Equal(TransactionStatus.Processing, t.Status);
        Assert.Equal(300m, t.BudgetDebited);
        Assert.Equal(200m, t.MainDebited);
    }

    [Fact]
    public void MarkProcessing_FromPending_SetsStatus()
    {
        var t = Transaction.CreateDeposit(Guid.NewGuid(), 1000m, "ref", "key-7");
        t.MarkProcessing();
        Assert.Equal(TransactionStatus.Processing, t.Status);
    }

    [Fact]
    public void MarkProcessing_WhenAlreadyProcessing_Throws()
    {
        var t = Transaction.CreateDeposit(Guid.NewGuid(), 1000m, "ref", "key-8");
        t.MarkProcessing();
        Assert.Throws<TransactionDomainException>(() => t.MarkProcessing());
    }

    // ── State: → Completed ────────────────────────────────────────────────────

    [Fact]
    public void Complete_FromPending_SetsCompleted()
    {
        var t = Transaction.CreateDeposit(Guid.NewGuid(), 2000m, "ref", "key-9");
        t.Complete();
        Assert.Equal(TransactionStatus.Completed, t.Status);
        Assert.NotNull(t.CompletedAt);
    }

    [Fact]
    public void Complete_FromProcessing_SetsCompleted()
    {
        var t = Transaction.CreateInPlatformPayment(
            Guid.NewGuid(), Guid.NewGuid(), 500m, "key-10");
        t.RecordDebitAndMarkProcessing(DebitSource.Budget, 500m, 0m);
        t.Complete();
        Assert.Equal(TransactionStatus.Completed, t.Status);
    }

    [Fact]
    public void Complete_AlreadyCompleted_IsIdempotent()
    {
        var t = Transaction.CreateDeposit(Guid.NewGuid(), 1000m, "ref", "key-11");
        t.Complete();
        t.Complete(); // Should not throw
        Assert.Equal(TransactionStatus.Completed, t.Status);
    }

    [Fact]
    public void Complete_WhenFailed_Throws()
    {
        var t = Transaction.CreateDeposit(Guid.NewGuid(), 1000m, "ref", "key-12");
        t.Fail("Payment gateway error");
        Assert.Throws<TransactionDomainException>(() => t.Complete());
    }

    // ── State: → Failed ───────────────────────────────────────────────────────

    [Fact]
    public void Fail_FromPending_SetsFailed()
    {
        var t = Transaction.CreateDeposit(Guid.NewGuid(), 1000m, "ref", "key-13");
        t.Fail("Webhook signature invalid");
        Assert.Equal(TransactionStatus.Failed, t.Status);
        Assert.Equal("Webhook signature invalid", t.FailureReason);
        Assert.NotNull(t.FailedAt);
    }

    [Fact]
    public void Fail_AlreadyFailed_IsIdempotent()
    {
        var t = Transaction.CreateDeposit(Guid.NewGuid(), 1000m, "ref", "key-14");
        t.Fail("First failure");
        t.Fail("Second failure"); // Should not throw
        Assert.Equal("First failure", t.FailureReason); // Keeps first reason
    }

    [Fact]
    public void Fail_WhenCompleted_Throws()
    {
        var t = Transaction.CreateDeposit(Guid.NewGuid(), 1000m, "ref", "key-15");
        t.Complete();
        Assert.Throws<TransactionDomainException>(() => t.Fail("Too late"));
    }

    // ── IsAwaitingRecipientCredit ─────────────────────────────────────────────

    [Fact]
    public void IsAwaitingRecipientCredit_ProcessingInPlatformPayment_True()
    {
        var t = Transaction.CreateInPlatformPayment(
            Guid.NewGuid(), Guid.NewGuid(), 500m, "key-16");
        t.RecordDebitAndMarkProcessing(DebitSource.Budget, 500m, 0m);
        Assert.True(t.IsAwaitingRecipientCredit);
    }

    [Fact]
    public void IsAwaitingRecipientCredit_Deposit_False()
    {
        var t = Transaction.CreateDeposit(Guid.NewGuid(), 1000m, "ref", "key-17");
        t.MarkProcessing();
        Assert.False(t.IsAwaitingRecipientCredit);
    }

    [Fact]
    public void IsAwaitingRecipientCredit_CompletedPayment_False()
    {
        var t = Transaction.CreateInPlatformPayment(
            Guid.NewGuid(), Guid.NewGuid(), 500m, "key-18");
        t.RecordDebitAndMarkProcessing(DebitSource.Budget, 500m, 0m);
        t.Complete();
        Assert.False(t.IsAwaitingRecipientCredit);
    }
}

// ── Full lifecycle integration tests (domain only) ────────────────────────────

public class TransactionLifecycleTests
{
    [Fact]
    public void DepositHappyPath_PendingToCompleted()
    {
        // payment.successful → Pending
        var t = Transaction.CreateDeposit(Guid.NewGuid(), 5000m, "PAY_001", "dep-key");
        Assert.Equal(TransactionStatus.Pending, t.Status);

        // wallet.credited → Completed
        t.Complete();
        Assert.Equal(TransactionStatus.Completed, t.Status);
        Assert.NotNull(t.CompletedAt);
    }

    [Fact]
    public void DepositFailurePath_PendingToFailed()
    {
        var t = Transaction.CreateDeposit(Guid.NewGuid(), 5000m, "PAY_002", "dep-fail");
        t.Fail("Paystack webhook signature invalid");
        Assert.Equal(TransactionStatus.Failed, t.Status);
        Assert.NotNull(t.FailedAt);
    }

    [Fact]
    public void InPlatformPaymentHappyPath_BudgetOnly()
    {
        // vendor.payment.approved → Pending
        var t = Transaction.CreateInPlatformPayment(
            Guid.NewGuid(), Guid.NewGuid(), 300m, "pay-key");
        Assert.Equal(TransactionStatus.Pending, t.Status);

        // wallet.budget.debited → Processing
        t.RecordDebitAndMarkProcessing(DebitSource.Budget, 300m, 0m);
        Assert.Equal(TransactionStatus.Processing, t.Status);
        Assert.Equal(300m, t.BudgetDebited);

        // wallet.credited (recipient) → Completed
        t.Complete();
        Assert.Equal(TransactionStatus.Completed, t.Status);
    }

    [Fact]
    public void InPlatformPaymentHappyPath_BudgetAndMainFallback()
    {
        var t = Transaction.CreateInPlatformPayment(
            Guid.NewGuid(), Guid.NewGuid(), 500m, "pay-key-2");

        // wallet.budget.debited (partial) → Processing
        t.RecordDebitAndMarkProcessing(DebitSource.Budget, 200m, 0m);
        Assert.Equal(200m, t.BudgetDebited);

        // wallet.main.debited (remainder) → still Processing, now with both amounts
        t.RecordDebitAndMarkProcessing(DebitSource.Main, 200m, 300m);
        Assert.Equal(200m, t.BudgetDebited);
        Assert.Equal(300m, t.MainDebited);

        t.Complete();
        Assert.Equal(TransactionStatus.Completed, t.Status);
    }

    [Fact]
    public void InPlatformPaymentFailure_InsufficientFunds()
    {
        var t = Transaction.CreateInPlatformPayment(
            Guid.NewGuid(), Guid.NewGuid(), 9999m, "pay-key-3");

        // wallet.insufficient_funds → Failed
        t.Fail("Insufficient funds. Main: ₦0.00, Budget: ₦0.00");
        Assert.Equal(TransactionStatus.Failed, t.Status);
        Assert.Contains("Insufficient funds", t.FailureReason);
    }

    [Fact]
    public void ExternalTransferHappyPath_PendingToCompleted()
    {
        // transfer.created → Pending
        var t = Transaction.CreateExternalTransfer(
            Guid.NewGuid(), 15000m, "TRF_ID_001", "trx-key");
        Assert.Equal(TransactionStatus.Pending, t.Status);
        Assert.Equal(DebitSource.Main, t.DebitSource);

        // transfer.completed → Completed
        t.Complete();
        Assert.Equal(TransactionStatus.Completed, t.Status);
    }

    [Fact]
    public void ExternalTransferFailurePath_PendingToFailed()
    {
        var t = Transaction.CreateExternalTransfer(
            Guid.NewGuid(), 15000m, "TRF_ID_002", "trx-key-2");

        // transfer.failed → Failed
        t.Fail("Bank account does not exist");
        Assert.Equal(TransactionStatus.Failed, t.Status);
        Assert.Equal("Bank account does not exist", t.FailureReason);
    }
}
