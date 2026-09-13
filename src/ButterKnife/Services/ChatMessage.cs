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

/// <summary>A text file or PDF attached to a message: the model sees <see cref="Text"/>, the transcript shows the name.</summary>
public sealed record ChatFile(string Name, string MediaType, int Size, string Text);

public sealed record ChatMessage(ChatRole Role, string Content, IReadOnlyList<ChatImage>? Images = null)
{
    public static readonly IReadOnlyList<ChatImage> NoImages = Array.Empty<ChatImage>();

    public static readonly IReadOnlyList<ChatFile> NoFiles = Array.Empty<ChatFile>();

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

    /// <summary>Attached documents; their text is folded into the content on the wire by <see cref="WithFilesAsText"/>.</summary>
    public IReadOnlyList<ChatFile> Files { get; init; } = NoFiles;

    public bool HasFiles => Files.Count > 0;

    /// <summary>
    /// The message as the model receives it: each attached file as a named document block ahead of the text.
    /// Every backend gets the same plain-text shape, so it works with any server. Images are kept.
    /// </summary>
    public ChatMessage WithFilesAsText()
    {
        if (!HasFiles)
        {
            return this;
        }

        var sb = new System.Text.StringBuilder();
        foreach (var file in Files)
        {
            sb.Append("<document name=\"").Append(file.Name.Replace("\"", "'")).Append("\">\n").Append(file.Text).Append("\n</document>\n\n");
        }
        sb.Append(Content);
        return new ChatMessage(Role, sb.ToString(), Images) { Id = Id, CreatedAt = CreatedAt, Reasoning = Reasoning, Model = Model, Stats = Stats };
    }

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
        return new ChatMessage(Role, content) { Files = Files };
    }
}
