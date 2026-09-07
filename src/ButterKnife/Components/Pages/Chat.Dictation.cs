using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ButterKnife.Components.Pages;

/// <summary>
/// Speech to text. Server mode records in the browser and posts the audio to the transcription connection; browser
/// mode uses the Web Speech API. The JS side is in <c>Chat.razor.js</c>; these are its .NET callbacks and the toggles.
/// </summary>
public partial class Chat
{
    // Dictation: server mode records audio and sends it to the transcription connection; browser mode uses SpeechRecognition.
    private enum DictationState { Idle, Recording, Transcribing }
    private const long MaxRecordingBytes = 25 * 1024 * 1024;
    private DictationState _dictation;
    private string? _dictationStatus;
    private bool _dictationError;
    private string _dictationBase = "";
    private string _dictationInterim = "";
    private bool _autoStop;
    private bool _autoSend;
    private bool _showDictationSettings;
    private ElementReference _micElement;

    private string DictationTitle => _dictation switch
    {
        DictationState.Recording => _autoStop ? "Recording — stops when you pause (click to stop now)" : "Stop recording",
        DictationState.Transcribing => "Transcribing…",
        _ => "Dictate (speech to text)",
    };

    private async Task ToggleDictationAsync()
    {
        if (_js is null)
        {
            return;
        }

        if (_dictation == DictationState.Recording)
        {
            await _js.InvokeVoidAsync("stopDictation");
            return; // JS calls back with OnRecordingReady (server mode) or OnDictationEnded (browser mode)
        }

        _dictationStatus = null;
        _dictationError = false;
        _dictationBase = _input.Length == 0 || _input.EndsWith(' ') || _input.EndsWith('\n') ? _input : _input + " ";
        _dictationInterim = "";

        var serverMode = await Transcription.GetConnectionAsync(_disposed.Token) is not null;
        var options = DictationOptions.Value;
        var started = await _js.InvokeAsync<string>("startDictation", serverMode, new
        {
            autoStop = _autoStop,
            silenceMs = options.SilenceDurationMs,
            maxSeconds = options.MaxRecordingSeconds,
        }, _micElement);
        switch (started)
        {
            case "server":
                _dictation = DictationState.Recording;
                _dictationStatus = _autoStop
                    ? "Recording… stops by itself when you pause, or click ■."
                    : "Recording… click ■ to stop and transcribe.";
                break;
            case "browser":
                _dictation = DictationState.Recording;
                _dictationStatus = "Listening (browser dictation)… click ■ to stop.";
                break;
            case "unsupported":
                _dictationError = true;
                _dictationStatus = "No speech recognition available: add a \"Whisper (speech to text)\" connection on the Connections page, or use a browser with built-in dictation (Chrome, Edge).";
                break;
            default:
                _dictationError = true;
                _dictationStatus = started.StartsWith("error:", StringComparison.Ordinal) ? started[6..] : started;
                break;
        }
    }

    /// <summary>Browser dictation: interim text is shown live, final text is committed as it arrives.</summary>
    [JSInvokable]
    public Task OnDictation(string finalText, string interimText)
    {
        if (finalText.Length > 0)
        {
            _dictationBase = (_dictationBase + finalText).TrimEnd() + " ";
        }
        _dictationInterim = interimText;
        _input = _dictationBase + _dictationInterim;
        return InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public Task OnDictationEnded(string? error)
    {
        _dictation = DictationState.Idle;
        _input = (_dictationBase + _dictationInterim).TrimEnd();
        _dictationInterim = "";
        _dictationError = error is not null;
        _dictationStatus = error;
        return InvokeAsync(StateHasChanged);
    }

    private async Task SaveDictationPrefsAsync(bool? autoStop = null, bool? autoSend = null)
    {
        _autoStop = autoStop ?? _autoStop;
        _autoSend = autoSend ?? _autoSend;
        if (_js is not null)
        {
            await _js.InvokeVoidAsync("saveDictationPrefs", new DictationPrefs(_autoStop, _autoSend));
        }
    }

    private sealed record DictationPrefs(bool? AutoStop, bool? AutoSend);

    /// <summary>Server dictation: pull the recording from the browser and send it to the transcription connection.</summary>
    [JSInvokable]
    public async Task OnRecordingReady(string contentType, long byteLength, string? reason = null)
    {
        _dictation = DictationState.Transcribing;
        _dictationStatus = reason switch
        {
            "silence" => "Pause detected — transcribing…",
            "max" => "Reached the recording limit — transcribing…",
            _ => "Transcribing…",
        };
        await InvokeAsync(StateHasChanged);

        try
        {
            if (byteLength == 0)
            {
                throw new InvalidOperationException("Nothing was recorded.");
            }

            var reference = await _js!.InvokeAsync<IJSStreamReference>("takeRecording", _disposed.Token);
            await using var stream = await reference.OpenReadStreamAsync(MaxRecordingBytes, _disposed.Token);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, _disposed.Token);
            buffer.Position = 0;

            var text = await Transcription.TranscribeAsync(buffer, contentType, _disposed.Token);
            _input = text.Length == 0 ? _dictationBase.TrimEnd() : _dictationBase + text;
            _dictationStatus = text.Length == 0 ? "The transcription came back empty." : null;
            _dictationError = text.Length == 0;

            if (_autoSend && text.Length > 0)
            {
                _dictation = DictationState.Idle;
                await InvokeAsync(StateHasChanged);
                await SendAsync();
                return;
            }
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _dictationError = true;
            _dictationStatus = $"Transcription failed: {ex.Message}";
        }
        finally
        {
            _dictation = DictationState.Idle;
        }

        await InvokeAsync(StateHasChanged);
        if (_js is not null)
        {
            await _js.InvokeVoidAsync("focus", _inputElement);
        }
    }
}
