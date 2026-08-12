namespace PaymentService.Domain.Enums;

public enum PaymentStatus
{
    Success,  // Paystack webhook verified and processed
    Failed    // Webhook received but invalid signature or processing error
}

public enum PaymentType
{
    Deposit   // Virtual account credit — the only type handled by Payment Service
}
