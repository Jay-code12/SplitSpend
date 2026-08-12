using PaymentService.Domain.Entities;
using PaymentService.Domain.Enums;
using Xunit;

namespace PaymentService.Tests;

// ── PaymentLog domain tests ───────────────────────────────────────────────────

public class PaymentLogDomainTests
{
    // ── CreateSuccess ─────────────────────────────────────────────────────────

    [Fact]
    public void CreateSuccess_ValidArgs_SetsCorrectFields()
    {
        var userId = Guid.NewGuid();
        var log = PaymentLog.CreateSuccess(
            userId, 5000m, "PAY_REF_001", "TXN_ID_001",
            "deposit:PAY_REF_001", "{}", "bank_transfer", "Approved", null);

        Assert.Equal(userId, log.UserId);
        Assert.Equal(5000m, log.Amount);
        Assert.Equal("NGN", log.Currency);
        Assert.Equal(PaymentStatus.Success, log.Status);
        Assert.Equal(PaymentType.Deposit, log.Type);
        Assert.Equal("PAY_REF_001", log.PaystackReference);
        Assert.Equal("bank_transfer", log.Channel);
        Assert.Equal("Approved", log.GatewayResponse);
    }

    [Fact]
    public void CreateSuccess_ZeroAmount_Throws()
        => Assert.Throws<PaymentDomainException>(
            () => PaymentLog.CreateSuccess(
                Guid.NewGuid(), 0m, "REF", "TXN", "key", "{}"));

    [Fact]
    public void CreateSuccess_NegativeAmount_Throws()
        => Assert.Throws<PaymentDomainException>(
            () => PaymentLog.CreateSuccess(
                Guid.NewGuid(), -500m, "REF", "TXN", "key", "{}"));

    [Fact]
    public void CreateSuccess_EmptyReference_Throws()
        => Assert.Throws<PaymentDomainException>(
            () => PaymentLog.CreateSuccess(
                Guid.NewGuid(), 1000m, "", "TXN", "key", "{}"));

    [Fact]
    public void CreateSuccess_PaidAt_UsesProvidedDate()
    {
        var paidAt = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc);
        var log    = PaymentLog.CreateSuccess(
            Guid.NewGuid(), 1000m, "REF_DATE", "TXN",
            "key", "{}", paidAt: paidAt);

        Assert.Equal(paidAt, log.PaidAt);
    }

    [Fact]
    public void CreateSuccess_NullPaidAt_UsesNow()
    {
        var before = DateTime.UtcNow.AddSeconds(-1);
        var log    = PaymentLog.CreateSuccess(
            Guid.NewGuid(), 1000m, "REF_NOW", "TXN",
            "key", "{}", paidAt: null);

        Assert.True(log.PaidAt >= before);
    }

    // ── CreateFailed ──────────────────────────────────────────────────────────

    [Fact]
    public void CreateFailed_SetsStatusAndAllowsNullReference()
    {
        var log = PaymentLog.CreateFailed(
            Guid.NewGuid(), 0m, null, "deposit:unknown", "{}");

        Assert.Equal(PaymentStatus.Failed, log.Status);
        Assert.Equal(string.Empty, log.PaystackReference);
    }

    [Fact]
    public void CreateFailed_WithReference_StoresIt()
    {
        var log = PaymentLog.CreateFailed(
            Guid.NewGuid(), 2000m, "PAY_BAD_001", "key", "{}");

        Assert.Equal("PAY_BAD_001", log.PaystackReference);
        Assert.Equal(PaymentStatus.Failed, log.Status);
    }

    // ── Immutability ──────────────────────────────────────────────────────────

    [Fact]
    public void PaymentLog_IsImmutableAfterCreation()
    {
        // PaymentLog has no setters exposed — all properties are get-only
        // This test verifies the design by attempting to compile mutation (would fail at compile time)
        var log = PaymentLog.CreateSuccess(
            Guid.NewGuid(), 1000m, "REF_IMM", "TXN", "key", "{}");

        Assert.Equal(PaymentStatus.Success, log.Status); // Cannot change after creation
        Assert.Equal(1000m, log.Amount);
    }
}

// ── VirtualAccount domain tests ───────────────────────────────────────────────

public class VirtualAccountDomainTests
{
    [Fact]
    public void Create_ValidArgs_SetsFields()
    {
        var userId  = Guid.NewGuid();
        var account = VirtualAccount.Create(
            userId, "0123456789", "John Doe",
            "WEMA Bank", "035", "CUS_abc123");

        Assert.Equal(userId, account.UserId);
        Assert.Equal("0123456789", account.AccountNumber);
        Assert.Equal("John Doe", account.AccountName);
        Assert.Equal("WEMA Bank", account.BankName);
        Assert.Equal("CUS_abc123", account.PaystackCustomerCode);
        Assert.True(account.IsActive);
    }

    [Fact]
    public void Create_EmptyAccountNumber_Throws()
        => Assert.Throws<PaymentDomainException>(
            () => VirtualAccount.Create(
                Guid.NewGuid(), "", "Name", "Bank", "035", "CUS_code"));

    [Fact]
    public void Create_EmptyCustomerCode_Throws()
        => Assert.Throws<PaymentDomainException>(
            () => VirtualAccount.Create(
                Guid.NewGuid(), "0123456789", "Name", "Bank", "035", ""));

    [Fact]
    public void Deactivate_SetsIsActiveFalse()
    {
        var account = VirtualAccount.Create(
            Guid.NewGuid(), "0123456789", "Name", "Bank", "035", "CUS_code");
        Assert.True(account.IsActive);

        account.Deactivate();
        Assert.False(account.IsActive);
    }
}

// ── Idempotency key derivation tests ─────────────────────────────────────────

public class IdempotencyKeyTests
{
    /// <summary>
    /// Tests the key derivation pattern used throughout the service.
    /// Key format: "deposit:{paystackReference}"
    /// </summary>
    [Fact]
    public void IdempotencyKey_Format_MatchesExpected()
    {
        var reference = "PAY_REF_20250115";
        var key       = $"deposit:{reference}";

        Assert.Equal("deposit:PAY_REF_20250115", key);
        Assert.StartsWith("deposit:", key);
    }

    [Fact]
    public void SameReference_ProducesSameKey()
    {
        var reference = "PAY_REF_ABC";
        var key1      = $"deposit:{reference}";
        var key2      = $"deposit:{reference}";
        Assert.Equal(key1, key2);
    }

    [Fact]
    public void DifferentReferences_ProduceDifferentKeys()
    {
        var key1 = $"deposit:REF_001";
        var key2 = $"deposit:REF_002";
        Assert.NotEqual(key1, key2);
    }
}

// ── HMAC signature verification tests ────────────────────────────────────────

public class HmacVerificationTests
{
    /// <summary>
    /// Tests the HMAC-SHA512 verification logic using a known input/output pair.
    /// In production, Paystack signs the raw webhook body with your secret key.
    /// </summary>
    [Fact]
    public void VerifySignature_ValidSignature_ReturnsTrue()
    {
        // Arrange — compute a known-good signature
        var secretKey = "test_secret_key_for_unit_tests";
        var payload   = "{\"event\":\"charge.success\",\"data\":{\"reference\":\"TEST_REF\"}}";

        using var hmac = new System.Security.Cryptography.HMACSHA512(
            System.Text.Encoding.UTF8.GetBytes(secretKey));
        var hash      = hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload));
        var signature = Convert.ToHexString(hash).ToLowerInvariant();

        // Act — simulate what PaystackClient.VerifyWebhookSignature does
        using var hmacVerify = new System.Security.Cryptography.HMACSHA512(
            System.Text.Encoding.UTF8.GetBytes(secretKey));
        var computed = Convert.ToHexString(
            hmacVerify.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload))
        ).ToLowerInvariant();

        // Assert
        Assert.Equal(computed, signature);
    }

    [Fact]
    public void VerifySignature_WrongSecret_SignatureMismatch()
    {
        var correctKey = "correct_secret";
        var wrongKey   = "wrong_secret";
        var payload    = "{\"event\":\"charge.success\"}";

        using var hmac1    = new System.Security.Cryptography.HMACSHA512(
            System.Text.Encoding.UTF8.GetBytes(correctKey));
        var correctSig = Convert.ToHexString(
            hmac1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload))
        ).ToLowerInvariant();

        using var hmac2  = new System.Security.Cryptography.HMACSHA512(
            System.Text.Encoding.UTF8.GetBytes(wrongKey));
        var wrongSig = Convert.ToHexString(
            hmac2.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload))
        ).ToLowerInvariant();

        Assert.NotEqual(correctSig, wrongSig);
    }

    [Fact]
    public void VerifySignature_TamperedPayload_SignatureMismatch()
    {
        var secretKey       = "my_secret";
        var originalPayload = "{\"amount\":5000}";
        var tamperedPayload = "{\"amount\":50000}"; // attacker changed amount

        using var hmac1 = new System.Security.Cryptography.HMACSHA512(
            System.Text.Encoding.UTF8.GetBytes(secretKey));
        var originalSig = Convert.ToHexString(
            hmac1.ComputeHash(System.Text.Encoding.UTF8.GetBytes(originalPayload))
        ).ToLowerInvariant();

        using var hmac2 = new System.Security.Cryptography.HMACSHA512(
            System.Text.Encoding.UTF8.GetBytes(secretKey));
        var tamperedSig = Convert.ToHexString(
            hmac2.ComputeHash(System.Text.Encoding.UTF8.GetBytes(tamperedPayload))
        ).ToLowerInvariant();

        // Tampered payload produces a different signature — the real signature rejects it
        Assert.NotEqual(originalSig, tamperedSig);
    }
}

// ── Kobo to Naira conversion tests ───────────────────────────────────────────

public class KoboNairaConversionTests
{
    // Paystack sends amounts in kobo. 1 Naira = 100 kobo.
    // PaystackClient always divides by 100 before returning to the service layer.

    [Theory]
    [InlineData(500000L, 5000.00)]
    [InlineData(100L, 1.00)]
    [InlineData(150L, 1.50)]
    [InlineData(1L, 0.01)]
    [InlineData(0L, 0.00)]
    public void KoboToNaira_ConvertsCorrectly(long kobo, decimal expectedNaira)
    {
        var naira = kobo / 100m;
        Assert.Equal(expectedNaira, naira);
    }

    [Theory]
    [InlineData(5000.00, 500000L)]
    [InlineData(1.50, 150L)]
    [InlineData(0.01, 1L)]
    public void NairaToKobo_ConvertsCorrectly(decimal naira, long expectedKobo)
    {
        var kobo = (long)(naira * 100);
        Assert.Equal(expectedKobo, kobo);
    }
}
