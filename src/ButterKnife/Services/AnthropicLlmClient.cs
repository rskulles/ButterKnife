using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Models.Messages;
using ButterKnife.Data;

namespace ButterKnife.Services;

/// <summary>
/// Anthropic Messages API through the official SDK. System messages are folded into the request's
/// top-level system prompt because the Messages API does not accept a "system" role in messages.
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient
{
    /// <summary>Streaming requests can afford generous room; hitting the cap truncates mid-thought.</summary>
    private const long MaxOutputTokens = 64000;

    private readonly LlmConnection _connection;
    private readonly AnthropicClient _client;

    public AnthropicLlmClient(LlmConnection connection, HttpClient? httpClient = null)
    {
        _connection = connection;
        _client = httpClient is null
            ? new AnthropicClient
            {
                ApiKey = connection.ApiKey ?? "",
                BaseUrl = connection.BaseUrl.TrimEnd('/'),
                Timeout = TimeSpan.FromMinutes(30),
            }
            : new AnthropicClient
            {
                ApiKey = connection.ApiKey ?? "",
                BaseUrl = connection.BaseUrl.TrimEnd('/'),
                Timeout = TimeSpan.FromMinutes(30),
                HttpClient = httpClient,
            };
    }

    public Guid ConnectionId => _connection.Id;

    public string BackendName => _connection.Name;

    public string? DefaultModel => _connection.DefaultModel;

    public async IAsyncEnumerable<ChatDelta> StreamChatAsync(
        string model,
        IReadOnlyList<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var system = string.Join("\n\n", messages.Where(m => m.Role == ChatRole.System).Select(m => m.Content));

        var chatMessages = messages
            .Where(m => m.Role != ChatRole.System)
            .Select(m => new MessageParam
            {
                Role = m.Role == ChatRole.User ? Role.User : Role.Assistant,
                Content = BuildContent(m),
            })
            .ToList();

        var parameters = system.Length > 0
            ? new MessageCreateParams { Model = model, MaxTokens = MaxOutputTokens, Messages = chatMessages, System = system }
            : new MessageCreateParams { Model = model, MaxTokens = MaxOutputTokens, Messages = chatMessages };

        var refused = false;
        int? inputTokens = null;
        int? outputTokens = null;

        await foreach (var streamEvent in _client.Messages.CreateStreaming(parameters, cancellationToken))
        {
            if (streamEvent.TryPickContentBlockDelta(out var blockDelta))
            {
                if (blockDelta.Delta.TryPickText(out var text))
                {
                    yield return ChatDelta.FromText(text.Text);
                }
                else if (blockDelta.Delta.TryPickThinking(out var thinking))
                {
                    yield return ChatDelta.FromReasoning(thinking.Thinking);
                }
            }
            else if (streamEvent.TryPickStart(out var start))
            {
                inputTokens = (int?)start.Message.Usage.InputTokens;
            }
            else if (streamEvent.TryPickDelta(out var messageDelta))
            {
                outputTokens = (int?)messageDelta.Usage.OutputTokens;
                var stop = messageDelta.Delta.StopReason;
                if (stop == StopReason.Refusal)
                {
                    refused = true;
                }
                else if (stop is not null)
                {
                    yield return ChatDelta.FromFinish(stop == StopReason.EndTurn || stop == StopReason.StopSequence ? FinishReason.Stop
                        : stop == StopReason.MaxTokens ? FinishReason.Length : FinishReason.Other);
                }
            }
        }

        if (inputTokens is not null || outputTokens is not null)
        {
            yield return ChatDelta.FromUsage(new TokenUsage(inputTokens, outputTokens));
        }

        if (refused)
        {
            throw new InvalidOperationException($"{BackendName}: the model declined this request (stop reason: refusal).");
        }
    }

    /// <summary>Text-only messages stay a plain string; with images, a block list (images first, then text).</summary>
    private static MessageParamContent BuildContent(ChatMessage message)
    {
        if (!message.HasImages)
        {
            return message.Content;
        }

        var blocks = new List<ContentBlockParam>();
        foreach (var image in message.Images)
        {
            blocks.Add(new ImageBlockParam
            {
                Source = new Base64ImageSource { MediaType = image.MediaType, Data = image.Base64 },
            });
        }
        if (message.Content.Length > 0)
        {
            blocks.Add(new TextBlockParam { Text = message.Content });
        }
        return blocks;
    }

    public async Task<int?> GetContextWindowAsync(string model, CancellationToken cancellationToken = default)
    {
        if (_connection.ContextWindow is { } configured)
        {
            return configured;
        }

        try
        {
            var info = await _client.Models.Retrieve(new Anthropic.Models.Models.ModelRetrieveParams { ModelID = model }, cancellationToken);
            return info.MaxInputTokens is > 0 ? (int?)info.MaxInputTokens : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Every current Claude model accepts images and the Models API has no capability field, so this is always true.</summary>
    public Task<bool?> SupportsImagesAsync(string model, CancellationToken cancellationToken = default) => Task.FromResult<bool?>(true);

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var ids = new List<string>();
        var page = await _client.Models.List(new Anthropic.Models.Models.ModelListParams(), cancellationToken);
        while (true)
        {
            ids.AddRange(page.Items.Select(m => m.ID));
            if (!page.HasNext())
            {
                break;
            }
            page = await page.Next();
        }
        return ids.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
