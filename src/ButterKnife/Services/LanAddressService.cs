using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace ButterKnife.Services;

/// <summary>Where a phone on the same network can reach this instance, and whether the server is even listening beyond localhost.</summary>
public sealed record PhoneAccess(bool ListensOnLan, IReadOnlyList<Uri> Urls);

/// <summary>
/// Works out the URLs to put in the "open on your phone" QR code: the address the browser used (when it is not a
/// loopback one, e.g. a .local hostname) followed by every LAN IPv4 address of this machine, all with the scheme,
/// port and path of the page the user is looking at.
/// </summary>
public sealed class LanAddressService(IServer server)
{
    public PhoneAccess ForPage(Uri currentPage)
    {
        var listening = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        return Compute(listening, LocalIPv4Addresses(), currentPage);
    }

    /// <summary>Pure core, so tests can feed in addresses. Private-range addresses come first, then other routable ones.</summary>
    public static PhoneAccess Compute(IEnumerable<string> serverAddresses, IEnumerable<IPAddress> localAddresses, Uri currentPage)
    {
        var listensOnLan = serverAddresses.Any(ListensBeyondLoopback);

        var urls = new List<Uri>();
        if (!IsLoopbackHost(currentPage.Host))
        {
            urls.Add(currentPage);
        }

        var candidates = localAddresses
            .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip) && !IsLinkLocal(ip))
            .Distinct()
            .OrderByDescending(IsPrivate)
            .ThenBy(ip => ip.ToString(), StringComparer.Ordinal);

        foreach (var ip in candidates)
        {
            var url = new UriBuilder(currentPage) { Host = ip.ToString() }.Uri;
            if (!urls.Contains(url))
            {
                urls.Add(url);
            }
        }

        return new PhoneAccess(listensOnLan, urls);
    }

    private static bool ListensBeyondLoopback(string address)
    {
        BindingAddress binding;
        try
        {
            binding = BindingAddress.Parse(address);
        }
        catch (FormatException)
        {
            return false;
        }

        if (binding.IsUnixPipe || binding.IsNamedPipe)
        {
            return false;
        }

        return binding.Host is "*" or "+" || !IsLoopbackHost(binding.Host);
    }

    private static bool IsLoopbackHost(string host)
    {
        var trimmed = host.Trim('[', ']');
        if (string.Equals(trimmed, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return IPAddress.TryParse(trimmed, out var ip) && IPAddress.IsLoopback(ip);
    }

    private static bool IsLinkLocal(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 169 && b[1] == 254;
    }

    private static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 192 && b[1] == 168) || (b[0] == 172 && b[1] >= 16 && b[1] <= 31);
    }

    private static IEnumerable<IPAddress> LocalIPv4Addresses()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            yield break;
        }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }
            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                yield return unicast.Address;
            }
        }
    }
}
