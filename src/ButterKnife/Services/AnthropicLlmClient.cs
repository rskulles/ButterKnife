using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
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
    private const int MaxOutputTokens = 64000;

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
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options ??= ChatOptions.Default;
        var system = string.Join("\n\n", messages.Where(m => m.Role == ChatRole.System).Select(m => m.Content));

        var chatMessages = BuildMessages(messages);

        // Temperature is not sent: current Claude models reject anything but the default. Extended thinking needs a
        // budget below max_tokens (at least 1024).
        var maxTokens = options.MaxTokens ?? MaxOutputTokens;
        ThinkingConfigParam? thinkingConfig = options.Think switch
        {
            true => new ThinkingConfigEnabled { BudgetTokens = Math.Clamp(maxTokens / 2, 1024, 32000) },
            false => new ThinkingConfigDisabled(),
            null => null,
        };
        var parameters = system.Length > 0
            ? new MessageCreateParams { Model = model, MaxTokens = maxTokens, Messages = chatMessages, System = system, Thinking = thinkingConfig }
            : new MessageCreateParams { Model = model, MaxTokens = maxTokens, Messages = chatMessages, Thinking = thinkingConfig };
        if (options.HasTools)
        {
            parameters = parameters with { Tools = options.Tools!.Select(ToTool).ToList() };
        }

        var refused = false;
        int? inputTokens = null;
        int? outputTokens = null;
        // tool_use blocks arrive as a start (id, name), input_json_delta fragments, then a stop; keyed by block index.
        var toolUses = new Dictionary<long, (string Id, string Name, StringBuilder Input)>();

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
                else if (blockDelta.Delta.TryPickInputJson(out var inputJson) && toolUses.TryGetValue(blockDelta.Index, out var partial))
                {
                    partial.Input.Append(inputJson.PartialJson);
                }
            }
            else if (streamEvent.TryPickContentBlockStart(out var blockStart) && blockStart.ContentBlock.TryPickToolUse(out var toolUse))
            {
                toolUses[blockStart.Index] = (toolUse.ID, toolUse.Name, new StringBuilder());
            }
            else if (streamEvent.TryPickContentBlockStop(out var blockStop) && toolUses.Remove(blockStop.Index, out var finished))
            {
                yield return ChatDelta.FromToolCall(new ToolCall(finished.Id, finished.Name, finished.Input.Length == 0 ? "{}" : finished.Input.ToString()));
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
                        : stop == StopReason.MaxTokens ? FinishReason.Length
                        : stop == StopReason.ToolUse ? FinishReason.ToolCalls : FinishReason.Other);
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

    /// <summary>
    /// The conversation without system messages. An assistant turn that called tools carries tool_use blocks after its
    /// text; tool results go back as tool_result blocks in a user message (consecutive results share one).
    /// </summary>
    private static List<MessageParam> BuildMessages(IReadOnlyList<ChatMessage> messages)
    {
        var result = new List<MessageParam>();
        List<ContentBlockParam>? pendingResults = null;

        foreach (var m in messages)
        {
            if (m.Role == ChatRole.System)
            {
                continue;
            }

            if (m.Role == ChatRole.Tool)
            {
                pendingResults ??= [];
                pendingResults.Add(new ToolResultBlockParam { ToolUseID = m.ToolCallId ?? "", Content = m.Content, IsError = m.IsError ? true : null });
                continue;
            }

            if (pendingResults is not null)
            {
                result.Add(new MessageParam { Role = Role.User, Content = pendingResults });
                pendingResults = null;
            }

            result.Add(new MessageParam
            {
                Role = m.Role == ChatRole.User ? Role.User : Role.Assistant,
                Content = m.HasToolCalls ? BuildToolUseContent(m) : BuildContent(m),
            });
        }

        if (pendingResults is not null)
        {
            result.Add(new MessageParam { Role = Role.User, Content = pendingResults });
        }
        return result;
    }

    private static MessageParamContent BuildToolUseContent(ChatMessage message)
    {
        var blocks = new List<ContentBlockParam>();
        if (message.Content.Length > 0)
        {
            blocks.Add(new TextBlockParam { Text = message.Content });
        }
        foreach (var call in message.ToolCalls)
        {
            var input = call.TryParseArguments() is { } arguments
                ? arguments.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                : new Dictionary<string, JsonElement>();
            blocks.Add(new ToolUseBlockParam { ID = call.Id, Name = call.Name, Input = input });
        }
        return blocks;
    }

    /// <summary>A ButterKnife tool as an Anthropic custom tool: the schema's properties and required list.</summary>
    private static ToolUnion ToTool(ToolDefinition tool)
    {
        var properties = tool.Parameters.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
            ? props.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
            : new Dictionary<string, JsonElement>();
        var required = tool.Parameters.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
            ? req.EnumerateArray().Select(e => e.GetString() ?? "").Where(n => n.Length > 0).ToList()
            : [];
        return new Tool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = new InputSchema { Properties = properties, Required = required },
        };
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

    /// <summary>Every current Claude model calls tools.</summary>
    public Task<bool?> SupportsToolsAsync(string model, CancellationToken cancellationToken = default) => Task.FromResult<bool?>(true);

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
