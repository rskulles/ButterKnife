using System.Net;
using ButterKnife.Services;

namespace ButterKnife.Tests;

public class LanAddressTests
{
    private static readonly Uri Page = new("http://localhost:5175/chat/abc");

    [Fact]
    public void DetectsWhetherServerListensBeyondLoopback()
    {
        Assert.False(LanAddressService.Compute(["http://localhost:5175"], [], Page).ListensOnLan);
        Assert.False(LanAddressService.Compute(["http://127.0.0.1:5175", "https://[::1]:7028"], [], Page).ListensOnLan);
        Assert.True(LanAddressService.Compute(["http://0.0.0.0:5175"], [], Page).ListensOnLan);
        Assert.True(LanAddressService.Compute(["http://[::]:5175"], [], Page).ListensOnLan);
        Assert.True(LanAddressService.Compute(["http://*:5175"], [], Page).ListensOnLan);
        Assert.True(LanAddressService.Compute(["http://localhost:5175", "http://192.168.1.20:5175"], [], Page).ListensOnLan);
        Assert.False(LanAddressService.Compute(["not an address"], [], Page).ListensOnLan);
    }

    [Fact]
    public void BuildsLanUrlsWithThePagesSchemePortAndPath_PrivateRangesFirst_SkippingLoopbackAndLinkLocal()
    {
        var access = LanAddressService.Compute(["http://0.0.0.0:5175"],
            [IPAddress.Parse("169.254.10.1"), IPAddress.Parse("203.0.113.9"), IPAddress.Parse("127.0.0.1"), IPAddress.Parse("192.168.1.20"), IPAddress.IPv6Loopback, IPAddress.Parse("10.0.0.5")],
            Page);

        Assert.Equal(
            ["http://10.0.0.5:5175/chat/abc", "http://192.168.1.20:5175/chat/abc", "http://203.0.113.9:5175/chat/abc"],
            access.Urls.Select(u => u.ToString()));
    }

    [Fact]
    public void KeepsTheBrowsersOwnHostFirstWhenItIsNotLoopback()
    {
        var page = new Uri("http://mymac.local:5175/");
        var access = LanAddressService.Compute(["http://0.0.0.0:5175"], [IPAddress.Parse("192.168.1.20")], page);

        Assert.Equal(["http://mymac.local:5175/", "http://192.168.1.20:5175/"], access.Urls.Select(u => u.ToString()));
    }
}
