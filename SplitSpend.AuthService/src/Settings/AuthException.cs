namespace SplitSpend.AuthService.Settings
{
// ── Domain exception ──────────────────────────────────────────────────────────

    /// <summary>
    /// Auth-domain exception that carries an HTTP status code.
    /// Caught by GlobalExceptionMiddleware and converted to a structured response.
    /// </summary>
    public sealed class AuthException(string message, int statusCode = 400)
    : Exception(message)
    {
        public int StatusCode { get; } = statusCode;
    }
}
