using GomokuClient.Web.Security;

namespace GomokuClient.Web.Tests;

public class AuthenticatorUriBuilderTests
{
    [Fact]
    public void Build_encodes_the_account_and_includes_standard_totp_parameters()
    {
        var uri = AuthenticatorUriBuilder.Build("alice+test@example.com", "JBSWY3DPEHPK3PXP");
        var parsed = new Uri(uri);
        var query = System.Web.HttpUtility.ParseQueryString(parsed.Query);

        Assert.Equal("otpauth", parsed.Scheme);
        Assert.Equal("totp", parsed.Host);
        Assert.Equal("Gomoku:alice+test@example.com", Uri.UnescapeDataString(parsed.AbsolutePath.TrimStart('/')));
        Assert.Equal("JBSWY3DPEHPK3PXP", query["secret"]);
        Assert.Equal("Gomoku", query["issuer"]);
        Assert.Equal("SHA1", query["algorithm"]);
        Assert.Equal("6", query["digits"]);
        Assert.Equal("30", query["period"]);
    }

    [Fact]
    public void Qr_renderer_returns_a_png_data_uri_without_external_request_urls()
    {
        var uri = QrCodeRenderer.ToPngDataUri("otpauth://totp/Gomoku%3Atest?secret=JBSWY3DPEHPK3PXP");

        Assert.StartsWith("data:image/png;base64,", uri);
        var png = Convert.FromBase64String(uri["data:image/png;base64,".Length..]);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
    }

    [Fact]
    public void Qr_renderer_rejects_empty_input()
    {
        Assert.Throws<ArgumentException>(() => QrCodeRenderer.ToPngDataUri(" "));
    }

    [Theory]
    [InlineData("", "JBSWY3DPEHPK3PXP")]
    [InlineData("alice@example.com", "")]
    public void Build_rejects_empty_account_or_secret(string account, string secret)
    {
        Assert.Throws<ArgumentException>(() => AuthenticatorUriBuilder.Build(account, secret));
    }
}
