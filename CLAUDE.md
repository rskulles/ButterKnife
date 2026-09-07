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
- `Services/ILlmClient` – the single abstraction over LLM backends. `StreamChatAsync` returns `IAsyncEnumerable<string>` of token deltas. Implementations are per wire protocol, not per model, and share `LlmClientBase` (named-client creation, error surfacing as `LlmException`, line-oriented stream reading):
  - `OllamaClient` – Ollama native API (`api/chat` NDJSON stream, `api/tags`). `BaseUrl` is the server root.
  - `OpenAiCompatibleClient` – `chat/completions` SSE stream and `models`. `BaseUrl` **must include** the version prefix (`…/v1`), matching the OpenAI SDK convention. Covers LM Studio, vLLM, llama.cpp server, and Ollama's `/v1` shim.
- `Services/MarkdownRenderer` – Markdig pipeline (advanced extensions, `DisableHtml()`) used to render assistant replies as `MarkupString`. Raw HTML is disabled deliberately: model output is untrusted and would otherwise be injected into the page. User messages are rendered as plain text. `Turn` in `Chat.razor` caches the rendered HTML until the next token arrives, so throttled re-renders during streaming do not re-parse older messages.
- `Services/LlmClientRegistry` – builds one client per configured backend (by `Kind`) and resolves them by name. `ModelCatalog` fans out to every backend for its model list; a backend that is down is returned in `BackendErrors` rather than failing the whole call.
- `Services/LlmServiceCollectionExtensions.AddLlmBackends` – the only place backends are wired: binds and validates `LlmOptions`, registers a named `HttpClient` per backend (name = backend `Name`), and applies BaseUrl/ApiKey/timeout through `IConfigureNamedOptions<HttpClientFactoryOptions>`.
- `Options/LlmOptions` – bound from the `Llm` section: a list of backends, each with `Name`, `Kind` (`Ollama` | `OpenAiCompatible`), `BaseUrl`, optional `ApiKey`, and `DefaultModel`. `DefaultModel` only affects which model is preselected in the UI.

**Key constraints:**

- All backend calls go through `IHttpClientFactory` named clients configured from `LlmOptions`; never `new HttpClient()`. Clients call `CreateClient` per request (not cached) so handler rotation keeps working. Timeout is `Timeout.InfiniteTimeSpan`; cancellation comes only from the caller's `CancellationToken`.
- Request bodies are serialised to a `StringContent` up front so they carry a `Content-Length`; some thin proxies and minimal servers cannot read chunked request bodies.
- Streaming must honour cancellation. The `CancellationToken` passed into `ILlmClient` is tied to the user's "Stop" button and to circuit disconnect; every `await` in the streaming path must forward it.
- Blazor Server UI updates from a streaming loop must call `InvokeAsync(StateHasChanged)`. `Chat.razor` throttles to one render per 50 ms (`RenderInterval`) rather than re-rendering on every token.
- Conversation state is per-circuit (scoped services / component fields). Do not put chat history in singletons; if persistence is added later it goes behind an explicit `IConversationStore`.
- Backend URLs and API keys live in `appsettings.Development.json` / user secrets (`dotnet user-secrets`), never in `appsettings.json` committed defaults beyond a placeholder like `http://ollama.local:11434`. Note that `appsettings.Development.json` *replaces* the `Backends` array index-by-index, it does not merge.
- The app is intended to be reachable from other devices on the LAN. Keep `--urls`/`ASPNETCORE_URLS` binding to `0.0.0.0` in mind; the SignalR circuit requires WebSockets, so any reverse proxy in front must forward `Upgrade` headers.

## Manual testing without a real LLM

`.claude/launch.json` has two configurations: `butterknife` (plain `dotnet run`) and `butterknife-fake-llm`, which starts the app with both backends pointed at `http://127.0.0.1:11434`. Pair the latter with a small stub server that answers `/api/tags`, `/api/chat` (NDJSON), `/v1/models` and `/v1/chat/completions` (SSE) on that port; the unit tests in `tests/ButterKnife.Tests/StubHttp.cs` show the exact wire shapes expected. Browser automation note: synthetic "Return" key presses may not reach the textarea's `keydown` listener; dispatch a `KeyboardEvent('keydown', {key: 'Enter'})` from JS instead when scripting the page.
