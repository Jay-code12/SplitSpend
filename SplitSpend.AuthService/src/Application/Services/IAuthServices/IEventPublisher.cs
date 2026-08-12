namespace SplitSpend.AuthService.Application.Services.IAuthServices
{
    public interface IEventPublisher
    {
        Task PublishUserRegisteredAsync(Guid credentialId, string email, string role, string correlationId, CancellationToken ct = default);
        Task PublishUserVerifiedAsync(Guid credentialId, Guid? userId, string email, string correlationId, CancellationToken ct = default);
        Task PublishUserLoggedInAsync(Guid? userId, string email, string ip, string device, string correlationId, CancellationToken ct = default);
    }
}
