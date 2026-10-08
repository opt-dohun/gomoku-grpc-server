using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace GomokuClient.Web.Tests;

public sealed class ReverseProxyHttpsTests
{
    [Fact]
    public async Task Trusted_proxy_https_reaches_login_without_redirect_and_emits_hsts()
    {
        using var factory = new ProductionProxyFactory("172.30.80.10");
        var response = await factory.SendAsync("172.30.80.10", "/Account/Login", "https");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
        Assert.False(response.Response.Headers.ContainsKey("Location"));
        Assert.Contains("max-age=", response.Response.Headers.StrictTransportSecurity.ToString());
    }
    [Theory]
    [InlineData("172.30.80.10")]
    [InlineData("::ffff:172.30.80.10")]
    [InlineData("172.30.80.11")]
    public async Task Trusted_https_sets_authentication_challenge_scheme(string remoteIp)
    {
        using var factory = new ProductionProxyFactory("172.30.80.10", "172.30.80.11");
        var response = await factory.SendAsync(remoteIp, "/", "https");

        Assert.Equal(StatusCodes.Status302Found, response.Response.StatusCode);
        Assert.Equal("https://gomoku.example.test/Account/Login?ReturnUrl=%2F",
            response.Response.Headers.Location.ToString());
        Assert.Contains("max-age=", response.Response.Headers.StrictTransportSecurity.ToString());
    }

    [Theory]
    [InlineData("172.30.80.99")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task Untrusted_peer_cannot_spoof_https_or_a_trusted_forwarded_address(string remoteIp)
    {
        using var factory = new ProductionProxyFactory("172.30.80.10");
        var response = await factory.SendAsync(remoteIp, "/Account/Login", "https", "172.30.80.10");
        AssertHttpsRedirect(response);
    }

    [Theory]
    [InlineData("172.30.80.10")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task Empty_allowlist_does_not_trust_any_peer(string remoteIp)
    {
        using var factory = new ProductionProxyFactory();
        var response = await factory.SendAsync(remoteIp, "/Account/Login", "https");
        AssertHttpsRedirect(response);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http")]
    [InlineData("https, http")]
    public async Task Trusted_proxy_without_external_https_redirects(string? forwardedProto)
    {
        using var factory = new ProductionProxyFactory("172.30.80.10");
        var response = await factory.SendAsync("172.30.80.10", "/Account/Login", forwardedProto);
        AssertHttpsRedirect(response);
    }

    [Fact]
    public async Task Forwarded_host_is_not_used_for_authentication_redirects()
    {
        using var factory = new ProductionProxyFactory("172.30.80.10");
        var response = await factory.SendAsync("172.30.80.10", "/", "https",
            forwardedHost: "attacker.example.test");
        Assert.Equal(StatusCodes.Status302Found, response.Response.StatusCode);
        Assert.StartsWith("https://gomoku.example.test/", response.Response.Headers.Location.ToString());
    }

    [Theory]
    [InlineData("*")]
    [InlineData("proxy")]
    [InlineData("172.30.80.0/24")]
    [InlineData("")]
    public void Invalid_proxy_configuration_fails_startup_with_setting_name(string proxy)
    {
        using var factory = new ProductionProxyFactory(proxy);
        var error = Assert.Throws<InvalidOperationException>(() => { _ = factory.Server; });
        Assert.Contains("ReverseProxy:KnownProxies", error.Message);
    }

    private static void AssertHttpsRedirect(HttpContext response)
    {
        Assert.Equal(StatusCodes.Status307TemporaryRedirect, response.Response.StatusCode);
        Assert.Equal("https://gomoku.example.test/Account/Login", response.Response.Headers.Location.ToString());
        Assert.False(response.Response.Headers.ContainsKey("Strict-Transport-Security"));
    }
}

internal sealed class ProductionProxyFactory(params string[] knownProxies) : WebApplicationFactory<Program>
{
    private readonly string _directory = Path.Combine(
        Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(),
        "gomoku-proxy-tests-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_directory);
        var tokenFile = Path.Combine(_directory, "backend.token");
        File.WriteAllText(tokenFile, "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        builder.UseSetting("BackendAuth:TokenFile", tokenFile);
        builder.UseEnvironment("Production");
        // Supply startup-time paths before the minimal host reads configuration.
        builder.UseSetting("Auth:DatabasePath", Path.Combine(_directory, "auth.db"));
        builder.UseSetting("Auth:DataProtectionKeysPath", Path.Combine(_directory, "keys"));
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["Auth:DatabasePath"] = Path.Combine(_directory, "auth.db"),
                ["Auth:DataProtectionKeysPath"] = Path.Combine(_directory, "keys"),
                ["GameServerUrl"] = "http://127.0.0.1:1",
                ["HTTPS_PORT"] = "443"
            };
            for (var i = 0; i < knownProxies.Length; i++)
                settings[$"ReverseProxy:KnownProxies:{i}"] = knownProxies[i];
            configuration.AddInMemoryCollection(settings);
        });
    }

    public Task<HttpContext> SendAsync(string remoteIp, string path, string? forwardedProto,
        string? forwardedFor = null, string? forwardedHost = null) =>
        Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
            context.Request.Method = "GET";
            context.Request.Scheme = "http";
            context.Request.Host = new HostString("gomoku.example.test");
            context.Request.Path = path;
            if (forwardedProto is not null)
                context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
            if (forwardedFor is not null)
                context.Request.Headers["X-Forwarded-For"] = forwardedFor;
            if (forwardedHost is not null)
                context.Request.Headers["X-Forwarded-Host"] = forwardedHost;
        });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_directory))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
