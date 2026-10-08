extern alias backend;
using InternalServiceAuth = backend::GomokuServer.Security.InternalServiceAuth;
using GomokuClient.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using GomokuGame.Proto;
using Grpc.Net.Client;
using Grpc.Core;

namespace GomokuClient.Web.Tests;

public sealed class BackendBoundaryTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(null, false)]
    [InlineData("wrong-token", false)]
    [InlineData(Token, true)]
    public void Backend_denies_requests_without_valid_service_token(string? presented, bool expected)
    {
        var context = new DefaultHttpContext();
        if (presented is not null) context.Request.Headers[InternalServiceAuth.HeaderName] = presented;
        Assert.Equal(expected, new InternalServiceAuth(Token).IsAuthorized(context.Request));
    }

    [Fact]
    public void Backend_denies_duplicate_token_headers()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[InternalServiceAuth.HeaderName] = new[] { Token, Token };
        Assert.False(new InternalServiceAuth(Token).IsAuthorized(context.Request));
    }

    [Fact]
    public async Task Backend_rejects_anonymous_grpc_and_sse_before_routing()
    {
        foreach (var path in new[] { "/gomoku.GameService/GetRoomList", "/sse/game/room-1" })
        {
            var context = new DefaultHttpContext();
            context.Request.Path = path;
            var reached = false;
            await new InternalServiceAuth(Token).InvokeAsync(context, _ => { reached = true; return Task.CompletedTask; });
            Assert.Equal(401, context.Response.StatusCode);
            Assert.False(reached);
        }
    }

    [Fact]
    public async Task Backend_allows_authenticated_requests_to_routing()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[InternalServiceAuth.HeaderName] = Token;
        var reached = false;
        await new InternalServiceAuth(Token).InvokeAsync(context, _ => { reached = true; return Task.CompletedTask; });
        Assert.True(reached);
    }

    [Fact]
    public void Backend_fails_closed_without_token_file()
    {
        using var factory = new WebApplicationFactory<InternalServiceAuth>();
        Assert.Throws<InvalidOperationException>(() => { _ = factory.Server; });
    }

    [Fact]
    public void Backend_fails_closed_with_invalid_token_file()
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), $"gomoku-invalid-{Guid.NewGuid():N}.token");
        File.WriteAllText(path, "short");
        try
        {
            using var factory = new WebApplicationFactory<InternalServiceAuth>()
                .WithWebHostBuilder(builder => builder.UseSetting("BackendAuth:TokenFile", path));
            Assert.Throws<InvalidOperationException>(() => { _ = factory.Server; });
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Running_backend_rejects_direct_sse_and_accepts_authenticated_sse()
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), $"gomoku-backend-{Guid.NewGuid():N}.token");
        File.WriteAllText(path, Token);
        try
        {
            using var factory = new WebApplicationFactory<InternalServiceAuth>()
                .WithWebHostBuilder(builder => builder.UseSetting("BackendAuth:TokenFile", path));
            using var client = factory.CreateClient();
            var anonymous = await client.GetAsync("/sse/game/unknown-room");
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, anonymous.StatusCode);
            client.DefaultRequestHeaders.Add(InternalServiceAuth.HeaderName, Token);
            var authenticated = await client.GetAsync("/sse/game/unknown-room");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, authenticated.StatusCode);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Running_backend_requires_service_token_for_grpc()
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), $"gomoku-grpc-{Guid.NewGuid():N}.token");
        File.WriteAllText(path, Token);
        try
        {
            using var factory = new WebApplicationFactory<InternalServiceAuth>()
                .WithWebHostBuilder(builder => builder.UseSetting("BackendAuth:TokenFile", path));
            using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() });
            var client = new GameService.GameServiceClient(channel);
            await Assert.ThrowsAsync<RpcException>(async () => await client.GetRoomListAsync(new Empty()));
            var rooms = await client.GetRoomListAsync(new Empty(), headers: new Metadata { { InternalServiceAuth.HeaderName.ToLowerInvariant(), Token } });
            Assert.Empty(rooms.Rooms);
            using var webChannel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
            {
                HttpHandler = new BackendTokenHandler(Token, factory.Server.CreateHandler())
            });
            var throughWebHandler = await new GameService.GameServiceClient(webChannel).GetRoomListAsync(new Empty());
            Assert.Empty(throughWebHandler.Rooms);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Web_sends_service_token_to_backend()
    {
        var sink = new CaptureHandler();
        using var client = new HttpClient(new BackendTokenHandler(Token, sink));
        using var response = await client.GetAsync("http://localhost/sse/game/example");
        Assert.Equal(Token, sink.Token);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Token { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Token = request.Headers.GetValues(InternalServiceAuth.HeaderName).Single();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
