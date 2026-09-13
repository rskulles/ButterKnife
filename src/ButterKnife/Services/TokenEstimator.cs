namespace ButterKnife.Services;

/// <summary>
/// Rough token estimate for a request before it is sent. Exact counts come back from the backend afterwards.
/// ~4 characters per token for English text; images are costly and vary by model, so a flat allowance is used.
/// </summary>
public static class TokenEstimator
{
    public const double CharsPerToken = 4.0;
    public const int TokensPerImage = 1000;
    public const int TokensPerMessageOverhead = 4;

    public static int Estimate(string text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / CharsPerToken);

    public static int Estimate(ChatMessage message) =>
        TokensPerMessageOverhead + Estimate(message.Content) + message.Images.Count * TokensPerImage + message.Files.Sum(f => Estimate(f.Text));

    public static int Estimate(IEnumerable<ChatMessage> messages) => messages.Sum(Estimate);
}
