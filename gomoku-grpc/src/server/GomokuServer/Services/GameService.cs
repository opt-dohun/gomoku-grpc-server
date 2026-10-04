using System.Collections.Concurrent;
using GomokuGame.Proto;
using GomokuServer.Domain.Services;
using GomokuServer.Infrastructure;
using Grpc.Core;

namespace GomokuServer.Services;

public class GameService : GomokuGame.Proto.GameService.GameServiceBase
{
    private readonly GameRoomManager _roomManager;
    private readonly GameLogicService _gameLogic;
    private readonly ILogger<GameService> _logger;

    public GameService(GameRoomManager roomManager, GameLogicService gameLogic, ILogger<GameService> logger)
    {
        _roomManager = roomManager;
        _gameLogic = gameLogic;
        _logger = logger;
    }

    // 생성한 방의 정보를 Task로 반환
    // unary : 단항 통신
    public override Task<CreateRoomResponse> CreateRoom(CreateRoomRequest request, ServerCallContext context)
    {
        var room = _roomManager.CreateRoom(request.RoomName, request.CreatorName);
        _logger.LogInformation($"방 생성: {room.Id} - {room.Name}");

        return Task.FromResult(new CreateRoomResponse
        {
            RoomId = room.Id,
            Creator = new PlayerInfo
            {
                PlayerId = room.Player1.Id,
                PlayerName = room.Player1.Name
            }
        });
    }

    public override Task<RoomList> GetRoomList(Empty request, ServerCallContext context)
    {
        var roomList = new RoomList();

        foreach (var room in _roomManager.GetAllRooms())
        {
            roomList.Rooms.Add(new RoomInfo
            {
                RoomId = room.Id,
                RoomName = room.Name,
                PlayerCount = room.Player2 == null ? 1 : 2,
                Status = room.Status
            });
        }

        return Task.FromResult(roomList);
    }

    public override Task<JoinRoomResponse> JoinRoom(JoinRoomRequest request, ServerCallContext context)
    {
        var room = _roomManager.GetRoom(request.RoomId);
        if (room == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "방을 찾을 수 없습니다."));
        }

        if (!room.TryAddPlayer(request.PlayerName, out var player))
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "방에 입장할 수 없습니다."));
        }

        room.Status = GameStatus.Playing;
        _logger.LogInformation($"{player!.Name}님이 {room.Name}에 입장했습니다.");

        return Task.FromResult(new JoinRoomResponse
        {
            RoomId = room.Id,
            Opponent = new PlayerInfo
            {
                PlayerId = room.Player1.Id,
                PlayerName = room.Player1.Name
            },
            BoardSize = room.Board.Size,
            IsPlayer = false,
            Joiner = new PlayerInfo
            {
                PlayerId = player.Id,
                PlayerName = player.Name
            }
        });
    }

    public override Task<GameState> GetGameState(RoomInfo request, ServerCallContext context)
    {
        var room = _roomManager.GetRoom(request.RoomId);
        if (room == null)
            throw new RpcException(new Status(StatusCode.NotFound, "방을 찾을 수 없습니다."));

        return Task.FromResult(_gameLogic.GetGameState(room));
    }

    public override async Task<GameState> PlaceStone(PlaceStoneRequest request, ServerCallContext context)
    {
        var room = _roomManager.GetRoom(request.RoomId);
        if (room == null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "방을 찾을 수 없습니다."));
        }

        var (success, message) = _gameLogic.PlaceStone(room, request.Position.Row, request.Position.Col, request.PlayerId);

        if (!success)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, message));
        }

        // 룸의 상태를 보고 승자ID를 결정합니다.
        var gameState = _gameLogic.GetGameState(room, room.Status == GameStatus.Finished ? request.PlayerId : null);

        // 현재 room에 존재하는 사용자들에게 실시간으로 게임 진행 상황을 전달합니다.
        // return은 현재 사용자에게 NotifyWatchers는 나머지 사용자들에게 정보 전달
        await NotifyAllWatcherAsync(request.RoomId, gameState);

        return gameState;
    }

    public override Task WatchGame(RoomInfo request, IServerStreamWriter<GameState> responseStream, ServerCallContext context)
    {
        throw new RpcException(new Status(StatusCode.Unimplemented, "Use SSE endpoint instead."));
    }

    public override async Task<GameState> Surrender(JoinRoomRequest request, ServerCallContext context)
    {
        var room = _roomManager.GetRoom(request.RoomId) ?? throw new RpcException(new Status(StatusCode.NotFound, "방을 찾을 수 없습니다."));
        room.Status = GameStatus.Finished;
        string winnerId = room.Player1.Name == request.PlayerName
            ? room.Player2!.Id
            : room.Player1.Id;
        room.WinnerId = winnerId;

        var gameState = _gameLogic.GetGameState(room, winnerId);
        await NotifyAllWatcherAsync(request.RoomId, gameState);

        return gameState;
    }

    // 양방향 스트리밍 (실시간 플레이)
    public override async Task PlayGame(IAsyncStreamReader<PlaceStoneRequest> requestStream,
        IServerStreamWriter<GameState> responseStream, ServerCallContext context)
    {
        await foreach (var request in requestStream.ReadAllAsync())
        {
            var room = _roomManager.GetRoom(request.RoomId);
            if (room == null) continue;

            var (success, _) = _gameLogic.PlaceStone(room, request.Position.Row, request.Position.Col, request.PlayerId);

            if (success)
            {
                var winnerId = room.Status == GameStatus.Finished ? request.PlayerId : null;
                var gameState = _gameLogic.GetGameState(room, winnerId);
                await responseStream.WriteAsync(gameState);
                await NotifyAllWatcherAsync(request.RoomId, gameState);
            }

        }
    }

    public Task NotifyAllWatcherAsync(string roomId, GameState gameState)
    {
        var room = _roomManager.GetRoom(roomId);
        if (room != null)
        {
            room.BroadcastGameState(gameState);
        }
        return Task.CompletedTask;
    }
}