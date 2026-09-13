using System.ComponentModel.DataAnnotations;
using ButterKnife.Data;
using ButterKnife.Options;
using ButterKnife.Services;
using Microsoft.AspNetCore.Components;

namespace ButterKnife.Components.Settings;

/// <summary>Connections section of Settings: list, presets, add/edit form, and the Test probe. Markup is in <c>ConnectionsSettings.razor</c>.</summary>
public partial class ConnectionsSettings : IDisposable
{
    [Inject] private IConnectionStore Store { get; set; } = default!;
    [Inject] private ConnectionEvents Events { get; set; } = default!;
    [Inject] private IHttpClientFactory HttpClientFactory { get; set; } = default!;
    [Inject] private TranscriptionClient Transcription { get; set; } = default!;
    [Inject] private LanScanner Scanner { get; set; } = default!;

    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    private IReadOnlyList<LlmConnection> _connections = [];
    private readonly Dictionary<Guid, (bool Ok, string Message)> _testResults = [];
    private List<string> _fetchedModels = [];

    private ConnectionForm _form = new();
    private Guid? _editingId;
    private bool _editingHasKey;
    private string? _presetHint;
    private string? _formMessage;
    private bool _formOk;
    private bool _busy;

    private bool _scanning;
    private ScanProgress _scanProgress;
    private IReadOnlyList<FoundServer>? _found;
    private string? _scanMessage;
    private CancellationTokenSource? _scanCts;

    protected override async Task OnInitializedAsync()
    {
        Events.Changed += OnChanged;
        await LoadAsync();
    }

    private async Task LoadAsync() => _connections = await Store.ListAsync();

    private void OnChanged() => _ = InvokeAsync(async () =>
    {
        await LoadAsync();
        StateHasChanged();
    });

    /// <summary>Runs the network scan, rendering progress at most a few times a second.</summary>
    private async Task ScanAsync()
    {
        if (_scanning)
        {
            return;
        }

        _scanning = true;
        _found = null;
        _scanMessage = null;
        _scanProgress = default;
        _scanCts = new CancellationTokenSource();
        var lastRender = Environment.TickCount64;
        var progress = new Progress<ScanProgress>(p =>
        {
            _scanProgress = p;
            if (Environment.TickCount64 - lastRender > 150 || p.Done == p.Total)
            {
                lastRender = Environment.TickCount64;
                StateHasChanged();
            }
        });

        try
        {
            _found = await Scanner.ScanAsync(progress, _scanCts.Token);
        }
        catch (OperationCanceledException)
        {
            _scanMessage = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            _scanMessage = $"Scan failed: {ex.Message}";
        }
        finally
        {
            _scanning = false;
            _scanCts.Dispose();
            _scanCts = null;
        }
    }

    private void CancelScan() => _scanCts?.Cancel();

    private bool AlreadyAdded(FoundServer server) =>
        _connections.Any(c => NormalizeUrl(c.BaseUrl) == NormalizeUrl(server.BaseUrl));

    /// <summary>localhost and 127.0.0.1 are the same box; trailing slashes and case do not matter.</summary>
    private static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url.Trim().TrimEnd('/').ToLowerInvariant();
        }
        var host = uri.IsLoopback ? "localhost" : uri.Host.ToLowerInvariant();
        return $"{uri.Scheme}://{host}:{uri.Port}{uri.AbsolutePath.TrimEnd('/')}";
    }

    /// <summary>Fills the form with a found server; the user presses Add (or edits first).</summary>
    private void UseFound(FoundServer server)
    {
        CancelEdit();
        var name = server.Name;
        if (_connections.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{server.Name} ({server.Host})";
        }
        _form = new ConnectionForm
        {
            Name = name,
            Kind = server.Kind,
            BaseUrl = server.BaseUrl,
            DefaultModel = server.Models.FirstOrDefault() ?? "",
        };
        _fetchedModels = server.Models.ToList();
        _presetHint = $"Found at {server.Host}:{server.Port}. Press Add to save it, or change the name first.";
        _formMessage = null;
    }

    private void ApplyPreset(ConnectionPreset preset)
    {
        _form.Name = preset.Name;
        _form.Kind = preset.Kind;
        _form.BaseUrl = preset.BaseUrl;
        _form.DefaultModel = preset.DefaultModel ?? "";
        _presetHint = preset.Hint;
        _formMessage = null;
    }

    private void BeginEdit(LlmConnection c)
    {
        _editingId = c.Id;
        _editingHasKey = c.HasApiKey;
        _form = new ConnectionForm
        {
            Name = c.Name,
            Kind = c.Kind,
            BaseUrl = c.BaseUrl,
            ApiKey = "",
            DefaultModel = c.DefaultModel ?? "",
            ContextWindow = c.ContextWindow,
        };
        _presetHint = null;
        _formMessage = null;
        _fetchedModels = [];
    }

    private void CancelEdit()
    {
        _editingId = null;
        _editingHasKey = false;
        _form = new ConnectionForm();
        _presetHint = null;
        _formMessage = null;
        _fetchedModels = [];
    }

    private async Task SaveAsync()
    {
        _busy = true;
        _formMessage = null;
        try
        {
            var apiKey = await ResolveApiKeyAsync();
            if (_editingId is { } id)
            {
                await Store.UpdateAsync(id, _form.Name, _form.Kind, _form.BaseUrl, apiKey, _form.DefaultModel, _form.ContextWindow);
            }
            else
            {
                await Store.CreateAsync(_form.Name, _form.Kind, _form.BaseUrl, apiKey, _form.DefaultModel, _form.ContextWindow);
            }

            Events.NotifyChanged();
            await LoadAsync();
            CancelEdit();
            _formOk = true;
            _formMessage = "Saved.";
        }
        catch (Exception ex)
        {
            _formOk = false;
            _formMessage = ex.Message;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Blank key on edit keeps the stored one unless the user asked to remove it.</summary>
    private async Task<string?> ResolveApiKeyAsync()
    {
        if (!string.IsNullOrWhiteSpace(_form.ApiKey))
        {
            return _form.ApiKey;
        }
        if (_editingId is { } id && !_form.ClearApiKey)
        {
            return (await Store.GetAsync(id))?.ApiKey;
        }
        return null;
    }

    private async Task DeleteAsync(LlmConnection c)
    {
        _busy = true;
        try
        {
            await Store.DeleteAsync(c.Id);
            _testResults.Remove(c.Id);
            if (_editingId == c.Id)
            {
                CancelEdit();
            }
            Events.NotifyChanged();
            await LoadAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task TestSavedAsync(LlmConnection c)
    {
        _busy = true;
        try
        {
            _testResults[c.Id] = await ProbeAsync(c);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Tests the form as typed, before saving; also fills the default-model suggestions.</summary>
    private async Task TestFormAsync()
    {
        if (!Uri.TryCreate(_form.BaseUrl, UriKind.Absolute, out _))
        {
            _formOk = false;
            _formMessage = "Enter an absolute base URL first.";
            return;
        }

        _busy = true;
        _formMessage = null;
        try
        {
            var apiKey = await ResolveApiKeyAsync();
            var candidate = new LlmConnection(
                _editingId ?? Guid.NewGuid(),
                string.IsNullOrWhiteSpace(_form.Name) ? "Untitled" : _form.Name,
                _form.Kind,
                _form.BaseUrl,
                apiKey,
                null,
                _form.ContextWindow,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);

            var (ok, message) = await ProbeAsync(candidate);
            _formOk = ok;
            _formMessage = message;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task<(bool Ok, string Message)> ProbeAsync(LlmConnection c)
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        try
        {
            IReadOnlyList<string> models;
            if (c.Kind == BackendKind.Transcription)
            {
                models = await Transcription.ProbeAsync(c, cts.Token);
                _fetchedModels = models.ToList();
                if (models.Count == 0)
                {
                    return (true, "Reachable. It did not list models (whisper.cpp does not), so try the microphone to confirm transcription works.");
                }
            }
            else
            {
                var client = LlmClientFactory.Create(c, HttpClientFactory);
                models = await client.ListModelsAsync(cts.Token);
                _fetchedModels = models.ToList();
            }

            if (models.Count == 0)
            {
                return (true, "Reachable, but it reports no models.");
            }

            var preview = string.Join(", ", models.Take(5));
            var more = models.Count > 5 ? $" and {models.Count - 5} more" : "";
            return (true, $"OK — {models.Count} model{(models.Count == 1 ? "" : "s")}: {preview}{more}.");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return (false, $"No response within {TestTimeout.TotalSeconds:0}s. Check the host, port and that the server is running.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string KindLabel(BackendKind kind) => kind switch
    {
        BackendKind.Ollama => "Ollama",
        BackendKind.OpenAiCompatible => "OpenAI-compatible",
        BackendKind.Anthropic => "Anthropic",
        BackendKind.Transcription => "Speech to text",
        _ => kind.ToString(),
    };

    private static string UrlHint(BackendKind kind) => kind switch
    {
        BackendKind.Ollama => "Server root, e.g. http://ollama.local:11434 (no /api suffix).",
        BackendKind.OpenAiCompatible => "Include the version prefix, e.g. http://lmstudio.local:1234/v1.",
        BackendKind.Anthropic => "Normally https://api.anthropic.com. Change only for a proxy.",
        BackendKind.Transcription => "OpenAI-style servers: include /v1 (http://whisper.local:8000/v1). whisper.cpp server: its root (http://whisper.local:8080); either works, the dialect is detected. Used only for the microphone button.",
        _ => "",
    };

    public void Dispose()
    {
        _scanCts?.Cancel();
        DisposeCore();
    }

    private void DisposeCore() => Events.Changed -= OnChanged;

    private sealed class ConnectionForm
    {
        [Required(ErrorMessage = "Name is required.")]
        public string Name { get; set; } = "";

        public BackendKind Kind { get; set; } = BackendKind.Ollama;

        [Required(ErrorMessage = "Base URL is required.")]
        [Url(ErrorMessage = "Enter an absolute http(s) URL.")]
        public string BaseUrl { get; set; } = "";

        public string ApiKey { get; set; } = "";

        public bool ClearApiKey { get; set; }

        public string DefaultModel { get; set; } = "";

        [Range(1, int.MaxValue, ErrorMessage = "Context window must be a positive number of tokens.")]
        public int? ContextWindow { get; set; }
    }
}
