namespace GomokuClient.Web.Security;

/// <summary>표준 TOTP 앱이 읽는 provisioning URI를 만든다. URI에는 비밀키가 있으므로 로그에 남기지 않는다.</summary>
public static class AuthenticatorUriBuilder
{
    public static string Build(string accountName, string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var label = Uri.EscapeDataString($"Gomoku:{accountName}");
        var issuer = Uri.EscapeDataString("Gomoku");
        return $"otpauth://totp/{label}?secret={Uri.EscapeDataString(secret)}&issuer={issuer}&algorithm=SHA1&digits=6&period=30";
    }
}
