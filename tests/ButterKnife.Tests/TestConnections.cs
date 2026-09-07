using ButterKnife.Data;
using ButterKnife.Options;

namespace ButterKnife.Tests;

internal static class TestConnections
{
    public static LlmConnection Make(string name, BackendKind kind, string baseUrl, string? defaultModel = null, string? apiKey = null) =>
        new(Guid.NewGuid(), name, kind, baseUrl, apiKey, defaultModel, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
