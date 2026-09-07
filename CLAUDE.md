# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

ButterKnife is a .NET Blazor Web App (Server interactivity) for chatting with LLMs hosted on the local network (e.g. Ollama, LM Studio, vLLM, llama.cpp server). The LLM backends are *not* on this machine; every backend is reached over HTTP at a configurable base URL.

Toolchain: .NET SDK 10.0 (`/usr/lib/dotnet/sdk`), target `net10.0`. Solution file is `ButterKnife.slnx` (XML solution format); projects are `src/ButterKnife` (web app) and `tests/ButterKnife.Tests` (xUnit 2.x — no `TestContext`, use `CancellationToken.None`).

## Commands

```bash
# Build / run
dotnet build
dotnet run --project src/ButterKnife
dotnet watch --project src/ButterKnife        # hot reload during UI work

# Expose on the LAN (default Kestrel binds to localhost only)
dotnet run --project src/ButterKnife --urls http://0.0.0.0:5000

# Tests
dotnet test
dotnet test --filter "FullyQualifiedName~OllamaClientTests"                       # one class
dotnet test --filter "FullyQualifiedName~OllamaClientTests.StreamsTokensFromNdjson" # one test

# Override backends without editing config (env vars use __ as the section separator)
Llm__Backends__0__BaseUrl=http://127.0.0.1:11434 dotnet run --project src/ButterKnife

# Formatting (analyzers run as part of build)
dotnet format
dotnet format --verify-no-changes
```

## Architecture

**Render mode.** Use Blazor Web App with `InteractiveServer` render mode globally (`@rendermode InteractiveServer` on `Routes`/`HeadOutlet` in `App.razor`). Chat UI needs a live SignalR circuit for token streaming, so do not mix in WebAssembly or Auto modes. There is no client project.

**Render mode.** `App.razor` sets `InteractiveServerRenderMode(prerender: false)` on both `Routes` and `HeadOutlet`. Prerendering is off on purpose: the chat page has nothing useful to show statically and prerendering would query every backend's model list twice per page load.

**Layering:**

- `Components/Pages/Chat.razor` (route `/`) – the chat page. Holds conversation state for the circuit, renders messages, and appends streamed tokens in place via a mutable `Turn` class. Its collocated `Chat.razor.js` module handles Enter-to-send and auto-scroll; Enter calls back into the component through a `DotNetObjectReference` (`[JSInvokable] SendFromKeyboardAsync`) rather than clicking the Send button, because that button is swapped for a Stop button while streaming and a captured element would go stale.
- **Connections** (`Data/LlmConnection`, `IConnectionStore`, `SqliteConnectionStore`) are the LLM servers, managed at runtime on the `/connections` page and stored in SQLite. `Kind` is `Ollama` | `OpenAiCompatible` | `Anthropic`. API keys are encrypted at rest with ASP.NET Core Data Protection (`IDataProtector`, purpose `ButterKnife.Connections.ApiKey`); if the key ring changes the key reads back as null and must be re-entered. `Llm:Backends` in config is **seed data only**: `ConnectionSeeder` (hosted service) copies it into the table on first start when the table is empty, then never again. `Services/ConnectionPresets` holds the stock endpoints (Ollama, LM Studio, OpenAI-compatible, Anthropic) the page offers.
- `Services/ILlmClient` – the single abstraction over a connection. `StreamChatAsync` returns `IAsyncEnumerable<string>` of token deltas. Implementations are per wire protocol and are built per use by `LlmClientFactory.Create(connection, httpClientFactory)`; `ILlmClientRegistry` resolves them from the store (`GetClientsAsync`, `GetAsync(connectionId)`):
  - `OllamaClient` – Ollama native API (`api/chat` NDJSON stream, `api/tags`). `BaseUrl` is the server root.
  - `OpenAiCompatibleClient` – `chat/completions` SSE stream and `models`. `BaseUrl` **must include** the version prefix (`…/v1`), matching the OpenAI SDK convention. Covers LM Studio, vLLM, llama.cpp server, and Ollama's `/v1` shim.
  - Both share `LlmClientBase`: one named `HttpClient` (`LlmClientBase.HttpClientName` = `"llm"`, infinite timeout), absolute URIs resolved from `BaseUrl`, `Bearer` auth when an API key is set, errors surfaced as `LlmException`.
  - `AnthropicLlmClient` – the official `Anthropic` NuGet SDK (`AnthropicClient`, `client.Messages.CreateStreaming`, `client.Models.List`). System messages are folded into the request's top-level `System` (the Messages API has no system role); `max_tokens` is 64000; a `refusal` stop reason is surfaced as an error. Tests inject an `HttpClient` into the SDK to stub the wire.
- **Images.** `ChatMessage.Images` holds `ChatImage(MediaType, byte[])` attachments; they are stored in `message_images` (BLOB, cascade on message delete) and re-sent with their message on every turn, so the model sees them in context. Each client encodes them natively: Ollama `images` (raw base64 array on the message), OpenAI-compatible `content` parts (`image_url` data URL, text part first), Anthropic `ImageBlockParam` with a base64 source before the text block. Text-only messages keep `content` as a plain string for compatibility. In `Chat.razor`, `InputFile` reads files over the circuit; files over ~1.5 MB or in unsupported formats are re-encoded in the browser as JPEG bounded to 1568 px via `RequestImageFileAsync`; at most 6 images per message, 10 MB each. Whether a model can actually see images is up to the model (e.g. gemma/qwen-VL variants); ButterKnife does not filter the picker.
- **Context meter.** `ILlmClient.StreamChatAsync` yields `ChatDelta` items: text deltas plus, when the backend reports it, a final `TokenUsage` (Ollama `prompt_eval_count`/`eval_count` on the done chunk; OpenAI-compatible via `stream_options.include_usage` on the last chunk; Anthropic `message_start.usage.input_tokens` + `message_delta.usage.output_tokens`). `GetContextWindowAsync(model)` returns the window or null: Ollama `/api/ps` `context_length` (what a loaded model actually runs with) then `/api/show` `*.context_length`; LM Studio's native `/api/v0/models/{id}` `max_context_length` at the server root; Anthropic Models API `max_input_tokens`. A connection's `ContextWindow` overrides all of that and is sent to Ollama as `options.num_ctx`. `Chat.razor` shows exact usage after a reply (prompt + completion) and a `TokenEstimator` estimate (~4 chars/token, ~1000/image) while typing or before the first reply; values persist on the conversation (`context_tokens`, `context_window`).
- **Compaction** (`Services/ConversationCompactor`) is client-side and backend-agnostic: the model summarises everything older than the last `Llm:CompactKeepRecentTurns` (default 4) turns; the summary is stored on the conversation (`summary`, `summary_through` = number of leading messages it covers) and `ComposeSystemPrompt` folds it into the system prompt of later requests, which then carry only the turns after the checkpoint. The stored transcript is never modified. Auto-compaction runs after a reply when reported usage ≥ `Llm:AutoCompactThreshold` (default 0.8) of a known window; the Compact button does the same on demand. The transcript shows a dashed checkpoint divider with the summary on click.
- **Dictation** (`Services/TranscriptionClient`, `TranscriptionService`). The microphone button in the composer has two paths. If a connection of kind `Transcription` exists (preset "Whisper (speech to text)"; any server with OpenAI's `/v1/audio/transcriptions`, BaseUrl includes `/v1`), the browser records with `MediaRecorder`, .NET pulls the bytes through an `IJSStreamReference` (`takeRecording`) and posts multipart `file` + `model` (+ `response_format=json`) to it; the text is appended to the textarea. Otherwise `Chat.razor.js` falls back to the Web Speech API (`SpeechRecognition`; Chrome/Edge only, and Chrome sends audio to Google) with interim results streamed to `OnDictation`. Transcription connections are not chat backends: `LlmClientFactory.IsChatBackend` filters them out of the registry and the model picker. `debugInjectRecording` in the JS module is a test hook to run the server path without a microphone.
- `Services/ModelCatalog` fans out to every connection for its model list; a connection that is down is returned in `BackendErrors` rather than failing the whole call. `ModelDescriptor` is keyed by `connectionId::model` and carries `IsDefault` (matches the connection's `DefaultModel`).
- `Services/LlmServiceCollectionExtensions.AddLlmBackends` wires options, the shared HttpClient, registry, catalog, markdown renderer and the seeder.
- `Options/LlmOptions` – `Backends` (seed only, see above) and `DefaultPersona`.
- `Services/MarkdownRenderer` – Markdig pipeline (advanced extensions, `DisableHtml()`) used to render assistant replies as `MarkupString`. Raw HTML is disabled deliberately: model output is untrusted and would otherwise be injected into the page. User messages are rendered as plain text. `Turn` in `Chat.razor` caches the rendered HTML until the next token arrives, so throttled re-renders during streaming do not re-parse older messages.
- `Services/LlmClientRegistry` – builds one client per configured backend (by `Kind`) and resolves them by name. `ModelCatalog` fans out to every backend for its model list; a backend that is down is returned in `BackendErrors` rather than failing the whole call.
- `Services/LlmServiceCollectionExtensions.AddLlmBackends` – the only place backends are wired: binds and validates `LlmOptions`, registers a named `HttpClient` per backend (name = backend `Name`), and applies BaseUrl/ApiKey/timeout through `IConfigureNamedOptions<HttpClientFactoryOptions>`.
- `Options/LlmOptions` – bound from the `Llm` section: a list of backends, each with `Name`, `Kind` (`Ollama` | `OpenAiCompatible`), `BaseUrl`, optional `ApiKey`, and `DefaultModel`. `DefaultModel` only affects which model is preselected in the UI.

**Key constraints:**

- HTTP-based clients go through the `IHttpClientFactory` named client `"llm"`; never `new HttpClient()`. Clients call `CreateClient` per request (not cached) so handler rotation keeps working. Timeout is `Timeout.InfiniteTimeSpan`; cancellation comes only from the caller's `CancellationToken`.
- Request bodies are serialised to a `StringContent` up front so they carry a `Content-Length`; some thin proxies and minimal servers cannot read chunked request bodies.
- Streaming must honour cancellation. The `CancellationToken` passed into `ILlmClient` is tied to the user's "Stop" button and to circuit disconnect; every `await` in the streaming path must forward it.
- Scoped CSS (`*.razor.css`) does not reach elements rendered by child components such as `NavLink`; use `::deep` for those (see `NavMenu.razor.css`).
- Blazor Server UI updates from a streaming loop must call `InvokeAsync(StateHasChanged)`. `Chat.razor` throttles to one render per 50 ms (`RenderInterval`) rather than re-rendering on every token.
- Conversations are persisted server-side through `Data/IConversationStore` (SQLite via `SqliteConversationStore`, plain ADO.NET). The `backend` column holds the **connection id** (GUID string); rows from before connections existed hold a name and resolve to `Guid.Empty`, so the chat shows "model no longer available" until the user picks one. The DB path comes from `Database:ConnectionString` (default `Data Source=data/butterknife.db`, relative to the content root; `src/ButterKnife/data/` is git-ignored). `Chat.razor` serves both `/` and `/chat/{id}`: the first send creates the conversation and navigates to its URL with `replace: true`, and `OnParametersSetAsync` reloads only when the id actually changes. The user message is saved before the request goes out; the assistant reply (or the partial text after Stop / disconnect) is saved when streaming ends. `ConversationEvents` is an in-process singleton the sidebar (`NavMenu.razor`) subscribes to so the list refreshes across circuits.
- System prompts come from **personas** (`Data/Persona`, `IPersonaStore`, `SqlitePersonaStore`). A conversation stores only `persona_id`; the persona's prompt is looked up at request time and prepended as a `ChatRole.System` message, never stored as a message row and never shown in the transcript. Built-in personas live in `DefaultPersonas.All` with **stable ids** (never change or reuse one) and are seeded with `INSERT OR IGNORE`, so users can edit them and edits survive restarts; they cannot be deleted. Custom personas are ordinary rows (`is_builtin = 0`) and deleting one sets `persona_id` to NULL on its conversations. `Llm:DefaultPersona` names the persona preselected for new chats. There is no persona management UI yet; the store already supports create/update/delete.
- `SqliteDatabase` owns the connection string (`Database:ConnectionString`), schema creation, seeding and forward-only migrations (`AddColumnIfMissingAsync` for columns added after first release). Both stores take it by constructor and open a connection per operation.
- Circuit state holds only what the page is showing; everything durable goes through the store.
- Connections (URLs, API keys) live in the database, edited on `/connections`. `Llm:Backends` in `appsettings*.json` only seeds an empty database; keep committed defaults to placeholders like `http://ollama.local:11434`. Note that `appsettings.Development.json` *replaces* the `Backends` array index-by-index, it does not merge.
- The app is intended to be reachable from other devices on the LAN. Keep `--urls`/`ASPNETCORE_URLS` binding to `0.0.0.0` in mind; the SignalR circuit requires WebSockets, so any reverse proxy in front must forward `Upgrade` headers.

## Manual testing without a real LLM

`.claude/launch.json` has two configurations: `butterknife` (plain `dotnet run`, port 5175) and `butterknife-fake-llm` (port 5176 so it can run beside a manually started instance), which starts the app with the seed backends pointed at `http://127.0.0.1:11434` (only matters for an empty database; otherwise add connections on `/connections`). Pair the latter with a small stub server that answers `/api/tags`, `/api/chat` (NDJSON), `/v1/models`, `/v1/chat/completions` (SSE) and, for the Anthropic kind, `/v1/messages` (Anthropic SSE events) on that port; the unit tests in `tests/ButterKnife.Tests/StubHttp.cs` show the exact wire shapes expected. Browser automation note: synthetic "Return" key presses may not reach the textarea's `keydown` listener; dispatch a `KeyboardEvent('keydown', {key: 'Enter'})` from JS instead when scripting the page.
