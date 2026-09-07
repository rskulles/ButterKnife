using System.Diagnostics;
using ButterKnife.Data;
using ButterKnife.Options;
using ButterKnife.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace ButterKnife.Components.Pages;

/// <summary>
/// The chat page. This file holds the core: state, lifecycle, conversation loading, model/persona selection and the
/// send/stream loop. Feature slices live in sibling partial files:
/// <c>Chat.Images.cs</c> (attachments), <c>Chat.Context.cs</c> (context meter + compaction), <c>Chat.Dictation.cs</c>
/// (speech to text). The transcript entries are <see cref="ChatTurn"/>. Markup is in <c>Chat.razor</c>.
/// </summary>
public partial class Chat : IAsyncDisposable
{
    [Inject] private ILlmClientRegistry Registry { get; set; } = default!;
    [Inject] private ModelCatalog Catalog { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private MarkdownRenderer Markdown { get; set; } = default!;
    [Inject] private IConversationStore Store { get; set; } = default!;
    [Inject] private IPersonaStore Personas { get; set; } = default!;
    [Inject] private ConversationEvents Events { get; set; } = default!;
    [Inject] private ConnectionEvents ConnectionEvents { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IOptions<LlmOptions> LlmOptions { get; set; } = default!;
    [Inject] private IOptions<DictationOptions> DictationOptions { get; set; } = default!;
    [Inject] private ConversationCompactor Compactor { get; set; } = default!;
    [Inject] private TranscriptionService Transcription { get; set; } = default!;

    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(50);

    private const int MaxTitleLength = 60;

    [Parameter] public Guid? Id { get; set; }

    private readonly List<ChatTurn> _turns = [];
    private Guid? _conversationId;
    private string? _title;
    private List<ModelDescriptor> _models = [];
    private IReadOnlyDictionary<string, string> _backendErrors = new Dictionary<string, string>();
    private string _selectedKey = "";
    private List<Persona> _personas = [];
    private string _personaKey = "";
    private bool _showPersonaPrompt;
    private string _input = "";
    private string? _error;
    private bool _isStreaming;
    private bool _loadingModels;

    private CancellationTokenSource? _generation;
    private readonly CancellationTokenSource _disposed = new();

    private ElementReference _messagesElement;
    private ElementReference _inputElement;

    private IJSObjectReference? _js;
    private DotNetObjectReference<Chat>? _self;

    private bool CanSend => !_isStreaming && (!string.IsNullOrWhiteSpace(_input) || _pendingImages.Count > 0) && Selected is not null;

    private ModelDescriptor? Selected => _models.FirstOrDefault(m => m.Key == _selectedKey);

    private Persona? SelectedPersona => _personas.FirstOrDefault(p => p.Id.ToString() == _personaKey);

    protected override async Task OnInitializedAsync()
    {
        _autoStop = DictationOptions.Value.AutoStopOnSilence;
        _autoSend = DictationOptions.Value.AutoSendAfterTranscription;
        ConnectionEvents.Changed += OnConnectionsChanged;
        await Task.WhenAll(RefreshModelsAsync(), LoadPersonasAsync());

        // First visit to "/": OnParametersSetAsync returns early (null id == null current id), so apply the default here.
        if (Id is null)
        {
            _personaKey = DefaultPersonaKey();
        }
    }

    private async Task LoadPersonasAsync()
    {
        try
        {
            _personas = (await Personas.ListAsync(_disposed.Token)).ToList();
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _error = $"Could not load personas: {ex.Message}";
        }
    }

    private string DefaultPersonaKey()
    {
        var name = LlmOptions.Value.DefaultPersona;
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }
        return _personas.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))?.Id.ToString() ?? "";
    }

    protected override async Task OnParametersSetAsync()
    {
        if (Id == _conversationId)
        {
            return; // same conversation (including the navigation we trigger ourselves after creating one)
        }

        Stop(); // navigating away mid-generation cancels it; the partial reply is persisted by SendAsync
        _turns.Clear();
        _error = null;
        _conversationId = Id;
        _title = null;
        _personaKey = DefaultPersonaKey();
        _compaction?.Cancel();
        _summary = null;
        _summaryThrough = 0;
        _contextTokens = null;
        _contextWindow = null;
        _showSummary = false;

        if (Id is not { } id)
        {
            _ = RefreshContextWindowAsync();
            return;
        }

        var conversation = await Store.GetAsync(id, _disposed.Token);
        if (conversation is null)
        {
            _conversationId = null;
            Nav.NavigateTo("/", replace: true);
            return;
        }

        _title = conversation.Title;
        _personaKey = conversation.PersonaId?.ToString() ?? "";
        _summary = conversation.Summary;
        _summaryThrough = Math.Min(conversation.SummaryThrough ?? 0, conversation.Messages.Count);
        _contextTokens = conversation.ContextTokens;
        _contextWindow = conversation.ContextWindow;
        foreach (var message in conversation.Messages)
        {
            _turns.Add(new ChatTurn(message.Role, message.Content, Markdown, message.Images));
        }

        var savedKey = ModelDescriptor.MakeKey(conversation.ConnectionId, conversation.Model);
        if (_models.Any(m => m.Key == savedKey))
        {
            _selectedKey = savedKey;
        }
        else if (_models.Count > 0)
        {
            _error = $"Model {conversation.Model} is no longer available on its connection; pick another to continue.";
        }

        _ = RefreshContextWindowAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _js = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Pages/Chat.razor.js");
            _self = DotNetObjectReference.Create(this);
            // The textarea element survives re-renders (only its attributes change), so one wiring is enough.
            await _js.InvokeVoidAsync("wireInput", _inputElement, _self);

            // Per-browser overrides of the dictation defaults.
            var prefs = await _js.InvokeAsync<DictationPrefs?>("loadDictationPrefs");
            if (prefs is not null)
            {
                _autoStop = prefs.AutoStop ?? _autoStop;
                _autoSend = prefs.AutoSend ?? _autoSend;
                StateHasChanged();
            }
        }
    }

    private async Task RefreshModelsAsync()
    {
        _loadingModels = true;
        _error = null;
        try
        {
            var result = await Catalog.GetModelsAsync(_disposed.Token);
            _models = result.Models.ToList();
            _backendErrors = result.BackendErrors;

            if (Selected is null)
            {
                _selectedKey = PickDefault()?.Key ?? "";
            }
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loadingModels = false;
        }
    }

    private ModelDescriptor? PickDefault() => _models.FirstOrDefault(m => m.IsDefault) ?? _models.FirstOrDefault();

    private void OnConnectionsChanged() =>
        _ = InvokeAsync(async () =>
        {
            await RefreshModelsAsync();
            StateHasChanged();
        });

    private void OnModelChanged(ChangeEventArgs e)
    {
        _selectedKey = e.Value?.ToString() ?? "";
        _ = RefreshContextWindowAsync();
    }

    private void NewChat() => Nav.NavigateTo("/");

    private void Stop() => _generation?.Cancel();

    /// <summary>Called from JS when Enter (without Shift) is pressed in the textarea.</summary>
    [JSInvokable]
    public Task SendFromKeyboardAsync() => SendAsync();

    private async Task SendAsync()
    {
        if (!CanSend || Selected is not { } model)
        {
            return;
        }

        var prompt = _input.Trim();
        var images = _pendingImages.ToArray();
        _input = "";
        _pendingImages.Clear();
        _imageError = null;
        _error = null;
        _contextTokens = null; // exact again once the backend reports usage for this request

        Guid conversationId;
        try
        {
            conversationId = await EnsureConversationAsync(model, prompt.Length > 0 ? prompt : $"Image ({images.Length})");
            await Store.AppendMessageAsync(conversationId, new ChatMessage(ChatRole.User, prompt, images), _disposed.Token);
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _error = $"Could not save the message: {ex.Message}";
            _input = prompt;
            _pendingImages.AddRange(images);
            return;
        }

        _turns.Add(new ChatTurn(ChatRole.User, prompt, Markdown, images));
        var history = BuildHistory();

        var reply = new ChatTurn(ChatRole.Assistant, "", Markdown) { IsStreaming = true };
        _turns.Add(reply);

        _isStreaming = true;
        _generation = CancellationTokenSource.CreateLinkedTokenSource(_disposed.Token);
        var token = _generation.Token;

        await RenderAndScrollAsync();

        string? completedText = null;
        TokenUsage? usage = null;
        try
        {
            var client = await Registry.GetAsync(model.ConnectionId, token);
            var lastRender = Stopwatch.GetTimestamp();

            await foreach (var delta in client.StreamChatAsync(model.Model, history, token))
            {
                if (delta.Usage is not null)
                {
                    usage = delta.Usage;
                    reply.Usage = usage;
                }
                if (delta.Text is not { } text)
                {
                    continue;
                }
                reply.Append(text);

                if (Stopwatch.GetElapsedTime(lastRender) >= RenderInterval)
                {
                    await RenderAndScrollAsync();
                    lastRender = Stopwatch.GetTimestamp();
                }
            }

            completedText = reply.Text;
            if (usage?.PromptTokens is { } promptTokens)
            {
                // What the next request will roughly start from: this prompt plus the reply we just got.
                _contextTokens = promptTokens + (usage.CompletionTokens ?? reply.Chunks);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            completedText = reply.Text; // keep whatever streamed before Stop / disconnect
            if (_disposed.IsCancellationRequested)
            {
                await PersistReplyAsync(conversationId, completedText);
                return; // circuit is gone; nothing to render
            }
            reply.Append(reply.Text.Length == 0 ? "(stopped)" : " (stopped)");
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            if (reply.Text.Length == 0)
            {
                _turns.Remove(reply);
            }
        }
        finally
        {
            reply.IsStreaming = false;
            _isStreaming = false;
            _generation?.Dispose();
            _generation = null;
        }

        await PersistReplyAsync(conversationId, completedText);
        await RenderAndScrollAsync();
        if (_js is not null)
        {
            await _js.InvokeVoidAsync("focus", _inputElement);
        }

        await AfterReplyContextAsync(conversationId, model);
    }

    /// <summary>Creates the conversation on first send and moves the URL to it; keeps the stored model in sync afterwards.</summary>
    private async Task<Guid> EnsureConversationAsync(ModelDescriptor model, string firstPrompt)
    {
        if (_conversationId is { } existing)
        {
            await Store.SetModelAsync(existing, model.ConnectionId, model.Model, _disposed.Token);
            return existing;
        }

        var title = MakeTitle(firstPrompt);
        var conversation = await Store.CreateAsync(title, model.ConnectionId, model.Model, SelectedPersona?.Id, _disposed.Token);
        _conversationId = conversation.Id;
        _title = title;
        Events.NotifyChanged();
        Nav.NavigateTo($"/chat/{conversation.Id}", replace: true); // same component instance; OnParametersSetAsync sees the id already loaded
        return conversation.Id;
    }

    /// <summary>Persists the persona for an existing conversation; a new conversation carries it when it is created.</summary>
    private async Task OnPersonaChangedAsync(ChangeEventArgs e)
    {
        _personaKey = e.Value?.ToString() ?? "";
        if (SelectedPersona is null)
        {
            _showPersonaPrompt = false;
        }

        if (_conversationId is not { } id)
        {
            return;
        }

        try
        {
            await Store.SetPersonaAsync(id, SelectedPersona?.Id, _disposed.Token);
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _error = $"Persona was not saved: {ex.Message}";
        }
    }

    private async Task PersistReplyAsync(Guid conversationId, string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            await Store.AppendMessageAsync(conversationId, new ChatMessage(ChatRole.Assistant, text), CancellationToken.None);
            Events.NotifyChanged();
        }
        catch (Exception ex)
        {
            _error = $"Reply was not saved: {ex.Message}";
        }
    }

    private static string MakeTitle(string prompt)
    {
        var firstLine = prompt.Split('\n', 2)[0].Trim();
        return firstLine.Length <= MaxTitleLength ? firstLine : firstLine[..MaxTitleLength].TrimEnd() + "…";
    }

    private async Task RenderAndScrollAsync()
    {
        await InvokeAsync(StateHasChanged);
        if (_js is not null)
        {
            try
            {
                await _js.InvokeVoidAsync("scrollToBottom", _messagesElement);
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        ConnectionEvents.Changed -= OnConnectionsChanged;
        if (_js is not null && _dictation == DictationState.Recording)
        {
            try
            {
                await _js.InvokeVoidAsync("stopDictation");
            }
            catch (JSDisconnectedException)
            {
            }
        }
        _disposed.Cancel();
        _generation?.Dispose();
        _compaction?.Dispose();
        _disposed.Dispose();
        _self?.Dispose();

        if (_js is not null)
        {
            try
            {
                await _js.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }
}
