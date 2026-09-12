namespace ButterKnife.Services;

/// <summary>
/// Applies the "reachable on the local network" setting to the configured listen addresses at startup. The
/// setting only decides the host part: on rewrites loopback hosts to the IPv4 wildcard, off rewrites wildcards
/// back to localhost. Ports and schemes come from --urls / ASPNETCORE_URLS / launchSettings as before.
/// </summary>
public static class ListenAddresses
{
    public const string DefaultUrls = "http://localhost:5000";

    public static IReadOnlyList<string> Apply(string? configuredUrls, bool listenOnLan)
    {
        var urls = (string.IsNullOrWhiteSpace(configuredUrls) ? DefaultUrls : configuredUrls)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var result = new List<string>();
        foreach (var url in urls)
        {
            BindingAddress binding;
            try
            {
                binding = BindingAddress.Parse(url);
            }
            catch (FormatException)
            {
                result.Add(url);
                continue;
            }

            if (binding.IsUnixPipe || binding.IsNamedPipe)
            {
                result.Add(url);
                continue;
            }

            var host = binding.Host;
            if (listenOnLan && IsLoopback(host))
            {
                host = "0.0.0.0";
            }
            else if (!listenOnLan && IsWildcard(host))
            {
                host = "localhost";
            }

            var rewritten = $"{binding.Scheme}://{host}:{binding.Port}{binding.PathBase}";
            if (!result.Contains(rewritten, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(rewritten);
            }
        }
        return result;
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "[::1]";

    private static bool IsWildcard(string host) => host is "*" or "+" or "0.0.0.0" or "[::]";
}
