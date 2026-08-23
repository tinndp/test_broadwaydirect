using BroadwayDirect.Core.Proxy;
using Xunit;

namespace BroadwayDirect.Tests;

public class ProxyUriTests
{
    [Fact]
    public void Normalize_ConvertsRawFormat()
    {
        Assert.Equal(
            "http://events_9a0bb1f1385b:QD4eTl82@82.29.144.169:61234",
            ProxyUri.Normalize("82.29.144.169:61234:events_9a0bb1f1385b:QD4eTl82"));
    }

    [Fact]
    public void Normalize_PassesThroughExistingUri()
    {
        const string uri = "http://alice:secret@1.2.3.4:8080";
        Assert.Equal(uri, ProxyUri.Normalize(uri));
    }

    [Fact]
    public void Normalize_RejectsMalformedInput()
    {
        Assert.Throws<FormatException>(() => ProxyUri.Normalize("not-a-valid-proxy"));
    }

    [Fact]
    public void Parse_ExtractsSwitchValueAndCredentials()
    {
        var parsed = ProxyUri.Parse("82.29.144.169:61234:events_9a0bb1f1385b:QD4eTl82");
        Assert.Equal("http://82.29.144.169:61234", parsed.SwitchValue);
        Assert.Equal("events_9a0bb1f1385b", parsed.User);
        Assert.Equal("QD4eTl82", parsed.Password);
    }

    [Fact]
    public void Parse_HandlesNoCredentials()
    {
        var parsed = ProxyUri.Parse("http://45.32.1.2:8080");
        Assert.Equal("http://45.32.1.2:8080", parsed.SwitchValue);
        Assert.Null(parsed.User);
    }
}
