namespace SplitSpend.AuthService.Settings
{
    public sealed class KafkaSettings
    {
        public required string BootstrapServers { get; init; }
        public required string GroupId { get; init; }

        // Topic names match the event naming convention from the MVP doc
        public required string UserRegisteredTopic { get; init; }
        public required string UserVerifiedTopic { get; init; } 
        public required string UserLoggedInTopic { get; init; } 
        public required string UserCreatedTopic { get; init; }  // consumed
    }
}
