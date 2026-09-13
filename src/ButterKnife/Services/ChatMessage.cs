namespace ButterKnife.Services;

public enum ChatRole
{
    System,
    User,
    Assistant,
}

/// <summary>An image attached to a message, held in memory as raw bytes. Sent to each backend in its native encoding.</summary>
public sealed record ChatImage(string MediaType, byte[] Data)
{
    public static readonly IReadOnlySet<string> SupportedMediaTypes =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/png", "image/gif", "image/webp" };

    private string? _base64;
    private string? _dataUrl;

    public string Base64 => _base64 ??= Convert.ToBase64String(Data);

    /// <summary>For &lt;img src&gt; and OpenAI-style image_url parts.</summary>
    public string DataUrl => _dataUrl ??= $"data:{MediaType};base64,{Base64}";
}

public sealed record ChatMessage(ChatRole Role, string Content, IReadOnlyList<ChatImage>? Images = null)
{
    public static readonly IReadOnlyList<ChatImage> NoImages = Array.Empty<ChatImage>();

    /// <summary>Row id when the message came from the store; null for messages built for a request.</summary>
    public long? Id { get; init; }

    /// <summary>When the message was stored; null for messages built for a request.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>The model's visible reasoning for an assistant reply, kept for display only; clients never send it back.</summary>
    public string? Reasoning { get; init; }

    /// <summary>Which model wrote an assistant reply; null for user messages and replies stored before this was recorded.</summary>
    public string? Model { get; init; }

    /// <summary>Timing and token counts of an assistant reply as measured when it streamed; null when unknown.</summary>
    public GenerationStats? Stats { get; init; }

    public IReadOnlyList<ChatImage> Images { get; init; } = Images ?? NoImages;

    public bool HasImages => Images.Count > 0;

    /// <summary>
    /// The same message with its images replaced by a note, for models that cannot see them. The stored
    /// transcript keeps the images; only the request loses them.
    /// </summary>
    public ChatMessage WithImagesAsText()
    {
        if (!HasImages)
        {
            return this;
        }

        var note = Images.Count == 1
            ? "[1 image was attached here but omitted: this model cannot see images.]"
            : $"[{Images.Count} images were attached here but omitted: this model cannot see images.]";
        var content = Content.Length == 0 ? note : $"{Content}\n\n{note}";
        return new ChatMessage(Role, content);
    }
}
