using System.Net;
using ButterKnife.Data;
using ButterKnife.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;

namespace ButterKnife.Tests;

public sealed class NetworkPinGateTests
{
    private readonly ManualTime _time = new();
    private readonly FakeSettings _settings = new();
    private readonly NetworkPinGate _gate;

    public NetworkPinGateTests()
    {
        _gate = new NetworkPinGate(_settings, new SettingsEvents(), new EphemeralDataProtectionProvider(), _time);
    }

    [Fact]
    public void HashVerifiesTheSamePinAndRejectsOthers()
    {
        var stored = NetworkPinGate.Hash("1234");

        Assert.StartsWith("pbkdf2-sha256$100000$", stored);
        Assert.True(NetworkPinGate.Verify("1234", stored));
        Assert.False(NetworkPinGate.Verify("1235", stored));
        Assert.False(NetworkPinGate.Verify("1234", "garbage"));
        Assert.NotEqual(stored, NetworkPinGate.Hash("1234")); // fresh salt each time
    }

    [Theory]
    [InlineData("123", "at least 4")]
    [InlineData(" 1234", "spaces")]
    public void ValidateExplainsBadPins(string pin, string expected) => Assert.Contains(expected, NetworkPinGate.Validate(pin));

    [Fact]
    public void ValidateAcceptsAnyCharacters() => Assert.Null(NetworkPinGate.Validate("correct horse battery"));

    [Fact]
    public async Task DisabledUntilAPinIsSetAndStoredAsAHash()
    {
        Assert.False(await _gate.IsEnabledAsync(CancellationToken.None));

        await _gate.SetPinAsync("4321", CancellationToken.None);

        Assert.True(await _gate.IsEnabledAsync(CancellationToken.None));
        Assert.DoesNotContain("4321", _settings.Values[SettingKeys.NetworkPinHash]);
        Assert.NotEmpty(_settings.Values[SettingKeys.NetworkPinStamp]);
        await Assert.ThrowsAsync<ArgumentException>(() => _gate.SetPinAsync("12", CancellationToken.None));
    }

    [Fact]
    public async Task TokensUnlockUntilTheyExpireOrThePinChanges()
    {
        await _gate.SetPinAsync("4321", CancellationToken.None);
        var token = _gate.IssueToken();

        Assert.True(_gate.IsValidToken(token));
        Assert.False(_gate.IsValidToken("nonsense"));
        Assert.False(_gate.IsValidToken(null));

        _time.Advance(TimeSpan.FromDays(29));
        Assert.True(_gate.IsValidToken(token));
        _time.Advance(TimeSpan.FromDays(2));
        Assert.False(_gate.IsValidToken(token));

        var fresh = _gate.IssueToken();
        await _gate.SetPinAsync("9999", CancellationToken.None);
        Assert.False(_gate.IsValidToken(fresh)); // the stamp changed
    }

    [Fact]
    public async Task WrongGuessesLockTheAddressOutForAMinute()
    {
        await _gate.SetPinAsync("4321", CancellationToken.None);
        var phone = IPAddress.Parse("10.0.0.7");
        var laptop = IPAddress.Parse("10.0.0.8");

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(UnlockOutcome.WrongPin, await _gate.TryUnlockAsync("0000", phone, CancellationToken.None));
        }
        Assert.Equal(UnlockOutcome.LockedOut, await _gate.TryUnlockAsync("4321", phone, CancellationToken.None));
        Assert.Equal(UnlockOutcome.Unlocked, await _gate.TryUnlockAsync("4321", laptop, CancellationToken.None)); // per address

        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(UnlockOutcome.Unlocked, await _gate.TryUnlockAsync("4321", phone, CancellationToken.None));
    }

    [Fact]
    public async Task RemovingThePinOpensTheDoor()
    {
        await _gate.SetPinAsync("4321", CancellationToken.None);
        await _gate.SetPinAsync(null, CancellationToken.None);

        Assert.False(await _gate.IsEnabledAsync(CancellationToken.None));
        Assert.False(_settings.Values.ContainsKey(SettingKeys.NetworkPinHash));
    }

    [Theory]
    [InlineData("127.0.0.1", "/chat/x", true)]
    [InlineData("::1", "/", true)]
    [InlineData("::ffff:127.0.0.1", "/", true)]
    [InlineData("10.0.0.7", "/unlock", true)]
    [InlineData("10.0.0.7", "/bootstrap_pulse.min.css", true)]
    [InlineData("10.0.0.7", "/_framework/blazor.web.js", true)]
    [InlineData("10.0.0.7", "/", false)]
    [InlineData("10.0.0.7", "/chat/5b8c", false)]
    [InlineData("10.0.0.7", "/backup", false)]
    [InlineData("10.0.0.7", "/_blazor", false)]
    public void ExemptsThisComputerTheUnlockPageAndStaticFiles(string ip, string path, bool exempt)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        context.Request.Path = path;

        Assert.Equal(exempt, NetworkPinGate.IsExempt(context));
    }

    [Theory]
    [InlineData("/chat/abc?x=1", "/chat/abc?x=1")]
    [InlineData("/", "/")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example/", "/")]
    public void OnlyLocalReturnUrlsAreFollowed(string? returnUrl, string expected) => Assert.Equal(expected, NetworkPinEndpoints.SafeReturnUrl(returnUrl));

    [Fact]
    public async Task MiddlewareRedirectsPagesRejectsTheRestAndLetsUnlockedDevicesThrough()
    {
        await _gate.SetPinAsync("4321", CancellationToken.None);
        var reached = 0;
        var middleware = new NetworkPinMiddleware(_ => { reached++; return Task.CompletedTask; }, _gate);

        var page = Context("10.0.0.7", "GET", "/chat/abc", accept: "text/html,*/*");
        page.Request.QueryString = new QueryString("?x=1");
        await middleware.InvokeAsync(page);
        Assert.Equal(302, page.Response.StatusCode);
        Assert.Equal("/unlock?returnUrl=%2Fchat%2Fabc%3Fx%3D1", page.Response.Headers.Location.ToString());

        var negotiate = Context("10.0.0.7", "POST", "/_blazor/negotiate", accept: "*/*");
        await middleware.InvokeAsync(negotiate);
        Assert.Equal(401, negotiate.Response.StatusCode);

        var backup = Context("10.0.0.7", "GET", "/backup", accept: "*/*");
        await middleware.InvokeAsync(backup);
        Assert.Equal(401, backup.Response.StatusCode);

        var unlocked = Context("10.0.0.7", "GET", "/chat/abc", accept: "text/html");
        unlocked.Request.Headers.Cookie = $"{NetworkPinGate.CookieName}={_gate.IssueToken()}";
        await middleware.InvokeAsync(unlocked);
        Assert.Equal(200, unlocked.Response.StatusCode);

        var local = Context("127.0.0.1", "GET", "/backup", accept: "*/*");
        await middleware.InvokeAsync(local);

        Assert.Equal(2, reached);
    }

    [Fact]
    public async Task MiddlewareIsTransparentWithoutAPin()
    {
        var reached = 0;
        var middleware = new NetworkPinMiddleware(_ => { reached++; return Task.CompletedTask; }, _gate);

        await middleware.InvokeAsync(Context("10.0.0.7", "GET", "/", accept: "text/html"));

        Assert.Equal(1, reached);
    }

    private static DefaultHttpContext Context(string ip, string method, string path, string accept)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.Headers.Accept = accept;
        return context;
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeSettings : ISettingsStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string? value, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Values.Remove(key);
            }
            else
            {
                Values[key] = value.Trim();
            }
            return Task.CompletedTask;
        }
    }
}
