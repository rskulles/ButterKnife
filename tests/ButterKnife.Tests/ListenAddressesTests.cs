using ButterKnife.Services;

namespace ButterKnife.Tests;

public class ListenAddressesTests
{
    [Fact]
    public void LanOn_RewritesLoopbackHostsToWildcard_KeepingPortsAndSchemes()
    {
        Assert.Equal(["http://0.0.0.0:5175"], ListenAddresses.Apply("http://localhost:5175", listenOnLan: true));
        Assert.Equal(["https://0.0.0.0:7028", "http://0.0.0.0:5175"], ListenAddresses.Apply("https://127.0.0.1:7028;http://localhost:5175", listenOnLan: true));
        Assert.Equal(["http://0.0.0.0:5175"], ListenAddresses.Apply("http://0.0.0.0:5175", listenOnLan: true));
        Assert.Equal(["http://192.168.1.5:5175"], ListenAddresses.Apply("http://192.168.1.5:5175", listenOnLan: true));
    }

    [Fact]
    public void LanOff_RewritesWildcardsToLocalhost()
    {
        Assert.Equal(["http://localhost:5175"], ListenAddresses.Apply("http://0.0.0.0:5175", listenOnLan: false));
        Assert.Equal(["http://localhost:5175"], ListenAddresses.Apply("http://*:5175", listenOnLan: false));
        Assert.Equal(["http://localhost:5175"], ListenAddresses.Apply("http://[::]:5175", listenOnLan: false));
        Assert.Equal(["http://localhost:5175"], ListenAddresses.Apply("http://localhost:5175", listenOnLan: false));
    }

    [Fact]
    public void FallsBackToTheDefaultUrl_AndCollapsesDuplicates()
    {
        Assert.Equal(["http://0.0.0.0:5000"], ListenAddresses.Apply(null, listenOnLan: true));
        Assert.Equal(["http://localhost:5000"], ListenAddresses.Apply("", listenOnLan: false));
        Assert.Equal(["http://0.0.0.0:5175"], ListenAddresses.Apply("http://localhost:5175;http://127.0.0.1:5175", listenOnLan: true));
    }
}
