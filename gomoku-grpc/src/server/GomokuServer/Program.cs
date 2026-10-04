using GomokuServer.Domain.Services;
using GomokuServer.Infrastructure;
using GomokuServer.Services;

var builder = WebApplication.CreateBuilder(args);

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