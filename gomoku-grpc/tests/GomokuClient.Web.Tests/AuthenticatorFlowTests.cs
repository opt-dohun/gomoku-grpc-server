using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GomokuClient.Web.Tests;

public sealed class AuthenticatorFlowTests : IClassFixture<TestWebApplicationFactory>
{
    private readonly TestWebApplicationFactory _factory;

    public AuthenticatorFlowTests(TestWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Registration_requires_enrollment_and_totp_then_login_requires_second_factor()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var protectedResponse = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, protectedResponse.StatusCode);
        Assert.Contains("/Account/Login", protectedResponse.Headers.Location!.OriginalString);

        var registerPage = await client.GetStringAsync("/Account/Register");
        var registerToken = AntiForgeryToken(registerPage);
        var password = "StrongPass!24680";
        var email = $"portfolio-{Guid.NewGuid():N}@example.test";
        var register = await client.PostAsync("/Account/Register", Form(
            ("__RequestVerificationToken", registerToken),
            ("Input.Email", email),
            ("Input.Password", password),
            ("Input.ConfirmPassword", password)));

        Assert.True(register.StatusCode == HttpStatusCode.Redirect, await register.Content.ReadAsStringAsync());
        Assert.Contains("EnableAuthenticator", register.Headers.Location!.OriginalString);

        var unverifiedGame = await client.GetAsync("/Game?roomId=not-a-member");
        Assert.Equal(HttpStatusCode.Redirect, unverifiedGame.StatusCode);
        Assert.Contains("EnableAuthenticator", unverifiedGame.Headers.Location!.OriginalString);

        var setupResponse = await client.GetAsync("/Account/EnableAuthenticator");
        Assert.Equal(HttpStatusCode.OK, setupResponse.StatusCode);
        var setupPage = await setupResponse.Content.ReadAsStringAsync();
        Assert.Contains("data:image/png;base64,", setupPage);
        Assert.Contains("no-store", setupResponse.Headers.CacheControl!.ToString(), StringComparison.OrdinalIgnoreCase);

        var code = await GenerateAuthenticatorCodeAsync(email);
        Assert.False(string.IsNullOrWhiteSpace(code));
        var enabled = await client.PostAsync("/Account/EnableAuthenticator", Form(
            ("__RequestVerificationToken", AntiForgeryToken(setupPage)),
            ("Input.Code", code)));
        Assert.True(enabled.StatusCode == HttpStatusCode.Redirect, await enabled.Content.ReadAsStringAsync());
        Assert.Equal("/", enabled.Headers.Location!.OriginalString);

        var logout = await client.PostAsync("/Account/Logout", Form(
            ("__RequestVerificationToken", AntiForgeryToken(setupPage))));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);

        var loginPage = await client.GetStringAsync("/Account/Login");
        var login = await client.PostAsync("/Account/Login", Form(
            ("__RequestVerificationToken", AntiForgeryToken(loginPage)),
            ("Input.Email", email),
            ("Input.Password", password),
            ("Input.RememberMe", "false")));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Contains("LoginWith2fa", login.Headers.Location!.OriginalString);

        var secondFactorPage = await client.GetStringAsync(login.Headers.Location);
        var secondFactor = await client.PostAsync(login.Headers.Location, Form(
            ("__RequestVerificationToken", AntiForgeryToken(secondFactorPage)),
            ("Input.Code", await GenerateAuthenticatorCodeAsync(email)),
            ("RememberMe", "false")));
        Assert.Equal(HttpStatusCode.Redirect, secondFactor.StatusCode);
        Assert.Equal("/", secondFactor.Headers.Location!.OriginalString);
    }

    private async Task<string> GenerateAuthenticatorCodeAsync(string email)
    {
        using var scope = _factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
        var user = await manager.FindByEmailAsync(email);
        Assert.NotNull(user);
        var key = await manager.GetAuthenticatorKeyAsync(user!);
        Assert.False(string.IsNullOrWhiteSpace(key));
        return CreateTotp(Base32Decode(key!));
    }

    // Tests produce the same standard 6-digit/30-second TOTP as an authenticator app.
    private static string CreateTotp(byte[] secret)
    {
        var counter = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        var hash = HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 0x0f;
        var number = ((hash[offset] & 0x7f) << 24) |
                     ((hash[offset + 1] & 0xff) << 16) |
                     ((hash[offset + 2] & 0xff) << 8) |
                     (hash[offset + 3] & 0xff);
        return (number % 1_000_000).ToString("D6");
    }

    private static byte[] Base32Decode(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var character in value.TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | alphabet.IndexOf(character);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((buffer >> bits) & 0xff));
            }
        }
        return output.ToArray();
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] values) =>
        new(values.Select(value => new KeyValuePair<string, string>(value.Key, value.Value)));

    private static string AntiForgeryToken(string html)
    {
        var match = Regex.Match(html, "<input[^>]*name=\\\"__RequestVerificationToken\\\"[^>]*value=\\\"([^\\\"]+)\\\"");
        Assert.True(match.Success, "The page should render a hidden antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
}

public sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _testDirectory = Path.Combine(
        Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), "gomoku-auth-tests-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_testDirectory);
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Auth:DatabasePath"] = Path.Combine(_testDirectory, "auth.db"),
                ["Auth:DataProtectionKeysPath"] = Path.Combine(_testDirectory, "keys"),
                ["GameServerUrl"] = "http://127.0.0.1:1"
            }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_testDirectory)) Directory.Delete(_testDirectory, recursive: true);
    }
}
