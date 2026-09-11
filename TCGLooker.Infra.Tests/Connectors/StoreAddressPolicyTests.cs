using System.Net;
using TCGLooker.Infra.Connectors;
using Xunit;

namespace TCGLooker.Infra.Tests.Connectors;

public sealed class StoreAddressPolicyTests
{
    [Theory]
    [InlineData("http://store.example")]
    [InlineData("https://localhost")]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://user:password@store.example")]
    [InlineData("https://store.example:8443")]
    public void TryNormalize_rejects_unsafe_store_addresses(string value)
    {
        var valid = StoreAddressPolicy.TryNormalize(new Uri(value), out _, out var error);

        Assert.False(valid);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryNormalize_keeps_only_the_https_origin()
    {
        var valid = StoreAddressPolicy.TryNormalize(
            new Uri("https://Store.Example/catalog/cards?q=charizard"),
            out var normalized,
            out _);

        Assert.True(valid);
        Assert.Equal("https://store.example/", normalized!.AbsoluteUri);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    public void IsPublic_rejects_non_public_networks(string value)
    {
        Assert.False(StoreAddressPolicy.IsPublic(IPAddress.Parse(value)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("2606:4700:4700::1111")]
    public void IsPublic_accepts_public_networks(string value)
    {
        Assert.True(StoreAddressPolicy.IsPublic(IPAddress.Parse(value)));
    }

    [Fact]
    public void IsSameOrigin_rejects_external_product_links()
    {
        var store = new Uri("https://store.example/");

        Assert.True(StoreAddressPolicy.IsSameOrigin(
            store, new Uri("https://store.example/?view=ecom/item&refid=1")));
        Assert.False(StoreAddressPolicy.IsSameOrigin(
            store, new Uri("https://internal.example/?view=ecom/item&refid=1")));
    }
}
