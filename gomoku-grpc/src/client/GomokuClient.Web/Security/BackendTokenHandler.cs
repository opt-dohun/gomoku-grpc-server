namespace GomokuClient.Web.Security;

/// <summary>gRPC 및 SSE 백엔드 요청에 서버 간 인증 토큰을 추가한다.</summary>
public sealed class BackendTokenHandler(string token, HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    public const string HeaderName = "X-Gomoku-Service-Token";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Add(HeaderName, token);
        return base.SendAsync(request, cancellationToken);
    }
}
