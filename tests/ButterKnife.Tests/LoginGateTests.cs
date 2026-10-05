using System.Net;
using System.Security.Claims;
using ButterKnife.Data;
using ButterKnife.Services;
using Microsoft.AspNetCore.Http;

namespace ButterKnife.Tests;

public sealed class LoginGateTests
{
    private readonly ManualTime _time = new();
    private readonly FakeUsers _users = new();
    private readonly LoginGate _gate;

    public LoginGateTests()
    {
        _gate = new LoginGate(_users, _time);
    }

    [Fact]
    public void HashVerifiesTheSamePasswordAndRejectsOthers()
    {
        var stored = PasswordHasher.Hash("1234");

        Assert.StartsWith("pbkdf2-sha256$100000$", stored);
        Assert.True(PasswordHasher.Verify("1234", stored));
        Assert.False(PasswordHasher.Verify("1235", stored));
        Assert.False(PasswordHasher.Verify("1234", "garbage"));
        Assert.NotEqual(stored, PasswordHasher.Hash("1234")); // fresh salt each time
    }

    [Theory]
    [InlineData("123", "at least 4")]
    [InlineData(" 1234", "spaces")]
    public void ValidateExplainsBadPasswords(string password, string expected) => Assert.Contains(expected, PasswordHasher.Validate(password));

    [Fact]
    public void ValidateAcceptsAnyCharacters() => Assert.Null(PasswordHasher.Validate("correct horse battery"));

    [Fact]
    public async Task WrongGuessesLockTheAddressOutForAMinute()
    {
        _users.Add("ann", "4321");
        var phone = IPAddress.Parse("10.0.0.7");
        var laptop = IPAddress.Parse("10.0.0.8");

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(LoginOutcome.WrongCredentials, (await _gate.TryLoginAsync("ann", "0000", phone, CancellationToken.None)).Outcome);
        }
        Assert.Equal(LoginOutcome.LockedOut, (await _gate.TryLoginAsync("ann", "4321", phone, CancellationToken.None)).Outcome);

        var (outcome, user) = await _gate.TryLoginAsync("ann", "4321", laptop, CancellationToken.None); // per address
        Assert.Equal(LoginOutcome.SignedIn, outcome);
        Assert.Equal("ann", user!.Username);

        _time.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(LoginOutcome.SignedIn, (await _gate.TryLoginAsync("ANN", "4321", phone, CancellationToken.None)).Outcome); // usernames ignore case
    }

    [Fact]
    public async Task UnknownUsersAndUsersWithoutAPasswordCannotSignIn()
    {
        var phone = IPAddress.Parse("10.0.0.7");

        Assert.Equal(LoginOutcome.WrongCredentials, (await _gate.TryLoginAsync("owner", "", phone, CancellationToken.None)).Outcome);
        Assert.Equal(LoginOutcome.WrongCredentials, (await _gate.TryLoginAsync("owner", "anything", phone, CancellationToken.None)).Outcome);
        Assert.Equal(LoginOutcome.WrongCredentials, (await _gate.TryLoginAsync("nobody", "4321", phone, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task PrincipalsCarryTheUserAndStayValidWhileTheStampMatches()
    {
        var ann = _users.Add("ann", "4321", admin: true);
        var principal = LoginGate.CreatePrincipal(ann, local: false);

        Assert.True(principal.Identity!.IsAuthenticated);
        Assert.Equal(ann.Id, LoginGate.UserId(principal));
        Assert.True(LoginGate.IsAdmin(principal));
        Assert.False(LoginGate.IsLocal(principal));
        Assert.True(await _gate.IsCurrentAsync(principal, CancellationToken.None));

        _users.Replace(ann with { SecurityStamp = "changed" });
        Assert.False(await _gate.IsCurrentAsync(principal, CancellationToken.None));

        _users.Remove(ann.Id);
        Assert.False(await _gate.IsCurrentAsync(principal, CancellationToken.None));

        var local = await _gate.LocalPrincipalAsync(CancellationToken.None);
        Assert.True(LoginGate.IsLocal(local));
        Assert.True(LoginGate.IsAdmin(local));
        Assert.Equal(_users.Owner.Id, LoginGate.UserId(local));
        Assert.False(LoginGate.IsAdmin(LoginGate.CreatePrincipal(_users.Add("bob", "1234"), local: false)));
    }

    [Theory]
    [InlineData("/login", true)]
    [InlineData("/logout", true)]
    [InlineData("/bootstrap_pulse.min.css", true)]
    [InlineData("/_framework/blazor.web.js", true)]
    [InlineData("/", false)]
    [InlineData("/chat/5b8c", false)]
    [InlineData("/backup", false)]
    [InlineData("/_blazor", false)]
    public void ExemptsTheLoginPageAndStaticFiles(string path, bool exempt)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        Assert.Equal(exempt, LoginGate.IsExempt(context));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("10.0.0.7", false)]
    [InlineData("192.168.1.20", false)]
    public void KnowsThisComputer(string ip, bool loopback) => Assert.Equal(loopback, LoginGate.IsLoopback(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("127.0.0.1", NetworkZone.Local)]
    [InlineData("::1", NetworkZone.Local)]
    [InlineData("::ffff:127.0.0.1", NetworkZone.Local)]
    [InlineData("192.168.1.20", NetworkZone.Lan)]
    [InlineData("::ffff:192.168.1.20", NetworkZone.Lan)]
    [InlineData("10.0.0.7", NetworkZone.Lan)]
    [InlineData("172.16.5.5", NetworkZone.Lan)]
    [InlineData("172.31.255.1", NetworkZone.Lan)]
    [InlineData("169.254.1.1", NetworkZone.Lan)]
    [InlineData("fd12:3456::1", NetworkZone.Lan)]
    [InlineData("fe80::1", NetworkZone.Lan)]
    [InlineData("172.32.0.1", NetworkZone.Remote)]
    [InlineData("100.100.1.1", NetworkZone.Remote)] // Tailscale's shared address space
    [InlineData("100.64.0.1", NetworkZone.Remote)]
    [InlineData("8.8.8.8", NetworkZone.Remote)]
    [InlineData("2001:db8::1", NetworkZone.Remote)]
    public void SortsAddressesIntoThisComputerTheHomeNetworkAndRemote(string ip, NetworkZone expected) =>
        Assert.Equal(expected, LoginGate.ZoneOf(IPAddress.Parse(ip)));

    [Fact]
    public void AdministeringNeedsTheRoleAndAPlaceInsideTheHouse()
    {
        var admin = LoginGate.CreatePrincipal(_users.Add("ann", "4321", admin: true), local: false);
        var user = LoginGate.CreatePrincipal(_users.Add("bob", "4321"), local: false);

        Assert.True(LoginGate.IsAdmin(admin));
        Assert.Equal(NetworkZone.Remote, LoginGate.ZoneOf(admin)); // never stamped: assume the worst
        Assert.False(LoginGate.CanAdminister(admin));

        LoginGate.StampZone(admin, NetworkZone.Lan);
        Assert.True(LoginGate.CanAdminister(admin));

        LoginGate.StampZone(admin, NetworkZone.Remote); // the laptop left the house: the next request re-stamps
        Assert.Equal(NetworkZone.Remote, LoginGate.ZoneOf(admin));
        Assert.False(LoginGate.CanAdminister(admin));
        Assert.Single(admin.FindAll(LoginGate.ZoneClaim)); // replaced, not piled up

        LoginGate.StampZone(user, NetworkZone.Local);
        Assert.False(LoginGate.CanAdminister(user)); // the right place, but not an administrator
    }

    [Theory]
    [InlineData("/chat/abc?x=1", "/chat/abc?x=1")]
    [InlineData("/", "/")]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example/", "/")]
    public void OnlyLocalReturnUrlsAreFollowed(string? returnUrl, string expected) => Assert.Equal(expected, LoginEndpoints.SafeReturnUrl(returnUrl));

    [Fact]
    public async Task MiddlewareSignsThisComputerInAsTheOwnerAndSendsOthersToSignIn()
    {
        var reached = new List<ClaimsPrincipal>();
        var middleware = new LoginMiddleware(context => { reached.Add(context.User); return Task.CompletedTask; }, _gate);

        var local = Context("127.0.0.1", "GET", "/backup", accept: "*/*");
        await middleware.InvokeAsync(local);
        Assert.Equal(_users.Owner.Id, LoginGate.UserId(Assert.Single(reached)));
        Assert.True(LoginGate.IsLocal(reached[0]));
        Assert.True(LoginGate.IsAdmin(reached[0]));
        Assert.Equal(NetworkZone.Local, LoginGate.ZoneOf(reached[0]));
        Assert.True(LoginGate.CanAdminister(reached[0]));

        var page = Context("10.0.0.7", "GET", "/chat/abc", accept: "text/html,*/*");
        page.Request.QueryString = new QueryString("?x=1");
        await middleware.InvokeAsync(page);
        Assert.Equal(302, page.Response.StatusCode);
        Assert.Equal("/login?returnUrl=%2Fchat%2Fabc%3Fx%3D1", page.Response.Headers.Location.ToString());

        var negotiate = Context("10.0.0.7", "POST", "/_blazor/negotiate", accept: "*/*");
        await middleware.InvokeAsync(negotiate);
        Assert.Equal(401, negotiate.Response.StatusCode);

        var backup = Context("10.0.0.7", "GET", "/backup", accept: "*/*");
        await middleware.InvokeAsync(backup);
        Assert.Equal(401, backup.Response.StatusCode);

        var stylesheet = Context("10.0.0.7", "GET", "/bootstrap_pulse.min.css", accept: "text/css");
        await middleware.InvokeAsync(stylesheet);
        Assert.Equal(2, reached.Count);
        Assert.False(reached[1].Identity?.IsAuthenticated ?? false); // static files pass through anonymously

        var ann = _users.Add("ann", "4321", admin: true);
        var signedIn = Context("10.0.0.7", "GET", "/chat/abc", accept: "text/html");
        signedIn.User = LoginGate.CreatePrincipal(ann, local: false); // what the cookie handler would have set
        await middleware.InvokeAsync(signedIn);
        Assert.Equal(200, signedIn.Response.StatusCode);
        Assert.Equal(3, reached.Count);
        Assert.Equal(ann.Id, LoginGate.UserId(reached[2]));
        Assert.False(LoginGate.IsLocal(reached[2]));
        Assert.Equal(NetworkZone.Lan, LoginGate.ZoneOf(reached[2])); // on the home network: may administer
        Assert.True(LoginGate.CanAdminister(reached[2]));

        var away = Context("100.101.102.103", "GET", "/settings/connections", accept: "text/html"); // Tailscale
        away.User = LoginGate.CreatePrincipal(ann, local: false);
        await middleware.InvokeAsync(away);
        Assert.Equal(200, away.Response.StatusCode); // signed in, so the page loads...
        Assert.Equal(NetworkZone.Remote, LoginGate.ZoneOf(reached[3]));
        Assert.True(LoginGate.IsAdmin(reached[3]));
        Assert.False(LoginGate.CanAdminister(reached[3])); // ...but the page itself refuses to administer from there
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

    /// <summary>In-memory users: the owner (no password) plus whoever a test adds. Only what the gate needs is implemented.</summary>
    private sealed class FakeUsers : IUserStore
    {
        private readonly List<(AppUser User, string? Password)> _rows = [];

        public FakeUsers()
        {
            Owner = Make("owner", "Roy", admin: true, owner: true);
            _rows.Add((Owner, null));
        }

        public AppUser Owner { get; }

        public AppUser Add(string username, string password, bool admin = false)
        {
            var user = Make(username, username, admin, owner: false);
            _rows.Add((user, password));
            return user;
        }

        public void Replace(AppUser user)
        {
            var index = _rows.FindIndex(r => r.User.Id == user.Id);
            _rows[index] = (user, _rows[index].Password);
        }

        public void Remove(Guid id) => _rows.RemoveAll(r => r.User.Id == id);

        private static AppUser Make(string username, string display, bool admin, bool owner) =>
            new(Guid.NewGuid(), username, display, admin, owner, HasPassword: !owner, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, null);

        public Task<IReadOnlyList<AppUser>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AppUser>>(_rows.Select(r => r.User).ToList());

        public Task<AppUser?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_rows.Select(r => r.User).FirstOrDefault(u => u.Id == id));

        public Task<AppUser?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default) =>
            Task.FromResult(_rows.Select(r => r.User).FirstOrDefault(u => string.Equals(u.Username, username.Trim(), StringComparison.OrdinalIgnoreCase)));

        public Task<AppUser> GetOwnerAsync(CancellationToken cancellationToken = default) => Task.FromResult(Owner);

        public Task<AppUser?> VerifyPasswordAsync(string username, string password, CancellationToken cancellationToken = default)
        {
            var row = _rows.FirstOrDefault(r => string.Equals(r.User.Username, username.Trim(), StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(row.User is not null && row.Password is not null && row.Password == password ? row.User : null);
        }

        public Task TouchLoginAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<AppUser> CreateAsync(string username, string displayName, string password, bool isAdmin, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task UpdateProfileAsync(Guid id, string username, string displayName, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SetPasswordAsync(Guid id, string? password, bool signOutEverywhere, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SetAdminAsync(Guid id, bool isAdmin, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
