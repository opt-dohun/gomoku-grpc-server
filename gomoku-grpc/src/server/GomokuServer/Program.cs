using GomokuServer.Domain.Services;
using GomokuServer.Infrastructure;
using GomokuServer.Services;
using GomokuServer.Security;

var builder = WebApplication.CreateBuilder(args);
var tokenFile = builder.Configuration["BackendAuth:TokenFile"];
if (string.IsNullOrWhiteSpace(tokenFile))
    throw new InvalidOperationException("BackendAuth:TokenFile 경로가 필요합니다.");
var serviceAuth = new InternalServiceAuth(File.ReadAllText(tokenFile).Trim());

// 서비스 등록
builder.Services.AddGrpc();
builder.Services.AddSingleton<GameRoomManager>();
builder.Services.AddSingleton<GameLogicService>();
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
    });
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.ConfigureEndpointDefaults(listenOptions =>
    {
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
    });
});

var app = builder.Build();

// gRPC unary 요청과 SSE 스트림을 동일한 웹→게임 서버 인증 경계로 보호한다.
app.Use((context, next) => serviceAuth.InvokeAsync(context, next));
app.UseCors();
app.MapGrpcService<GameService>();
app.MapGet("/", () => "gRPC 오목 게임 서버가 실행 중입니다.");

app.MapGet("/sse/game/{roomId}", async (string roomId, HttpContext context, GameRoomManager roomManager, GameLogicService gameLogic) =>
{
    var room = roomManager.GetRoom(roomId);
    if (room == null)
    {
        context.Response.StatusCode = 404;
        return;
    }

    context.Response.Headers.Append("Content-Type", "text/event-stream");
    context.Response.Headers.Append("Cache-Control", "no-cache");
    context.Response.Headers.Append("Connection", "keep-alive");

    var channel = System.Threading.Channels.Channel.CreateUnbounded<GomokuGame.Proto.GameState>();

    lock (room.RoomLock)
    {
        room.Watchers.Add(channel);
        var initialState = gameLogic.GetGameState(room);
        channel.Writer.TryWrite(initialState);
    }

    try
    {
        await foreach (var state in channel.Reader.ReadAllAsync(context.RequestAborted))
        {
            var json = Google.Protobuf.JsonFormatter.Default.Format(state);
            await context.Response.WriteAsync($"data: {json}\n\n");
            await context.Response.Body.FlushAsync();
        }
    }
    catch (OperationCanceledException)
    {
        // Client disconnected
    }
    finally
    {
        lock (room.RoomLock)
        {
            room.Watchers.Remove(channel);
        }
    }
});

app.Run();