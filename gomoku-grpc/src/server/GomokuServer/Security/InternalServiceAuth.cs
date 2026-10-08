using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace GomokuServer.Security;

/// <summary>인증된 웹 백엔드만 gRPC와 SSE 엔드포인트에 접근하게 한다.</summary>
public sealed class InternalServiceAuth
{
    public const string HeaderName = "X-Gomoku-Service-Token";
    private readonly byte[] _expected;

    public InternalServiceAuth(string token)
    {
        if (token.Length != 64 || !token.All(Uri.IsHexDigit))
            throw new InvalidOperationException("서비스 토큰은 32바이트 난수의 hex 인코딩이어야 합니다.");
        _expected = Convert.FromHexString(token);
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!IsAuthorized(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        await next(context);
    }

    public bool IsAuthorized(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(HeaderName, out var values) || values.Count != 1) return false;
        var value = values[0];
        if (value is null || value.Length != 64) return false;
        try
        {
            var actual = Convert.FromHexString(value);
            return CryptographicOperations.FixedTimeEquals(actual, _expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
