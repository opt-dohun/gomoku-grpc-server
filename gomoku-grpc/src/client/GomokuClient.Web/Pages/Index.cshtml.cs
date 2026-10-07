using GomokuClient.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using GomokuClient.Web.Services;

namespace GomokuClient.Web.Pages;

[Authorize]
public class IndexModel : MfaRequiredPageModel
{
    private readonly GrpcGameClient _gameClient;
    private readonly ILogger<IndexModel> _logger;
    private readonly AuthDbContext _authDb;

    public IndexModel(GrpcGameClient gameClient, ILogger<IndexModel> logger, UserManager<IdentityUser> userManager, AuthDbContext authDb)
        : base(userManager)
    {
        _gameClient = gameClient;
        _logger = logger;
        _authDb = authDb;
    }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnGetRoomList()
    {
        try
        {
            var roomList = await _gameClient.GetRoomListAsync();
            var mappedRooms = roomList.Rooms.Select(r => new {
                roomId = r.RoomId,
                roomName = r.RoomName,
                playerCount = r.PlayerCount,
                status = (int)r.Status
            });
            return new JsonResult(new { rooms = mappedRooms });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "방 목록 조회 실패");
            return new JsonResult(new { rooms = Array.Empty<object>() });
        }
    }

    public async Task<IActionResult> OnPostCreateRoom([FromBody] CreateRoomModel model)
    {
        try
        {
            var response = await _gameClient.CreateRoomAsync(model.RoomName, model.PlayerName);
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId)) return Unauthorized();

            _authDb.GameMemberships.Add(new GameMembership
            {
                UserId = userId,
                RoomId = response.RoomId,
                PlayerId = response.Creator.PlayerId,
                PlayerName = model.PlayerName,
                IsPlayer1 = true
            });
            await _authDb.SaveChangesAsync();
            return new JsonResult(new {
                success = true, 
                roomId = response.RoomId,
                playerId = response.Creator.PlayerId
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "방 생성 실패");
            return new JsonResult(new { success = false });
        }
    }

    public async Task<IActionResult> OnPostJoinRoom([FromBody] JoinRoomModel model)
    {
        try
        {
            var response = await _gameClient.JoinRoomAsync(model.RoomId, model.PlayerName);
            if (response != null)
            {
                var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrEmpty(userId)) return Unauthorized();
                var membership = new GameMembership
                {
                    UserId = userId,
                    RoomId = model.RoomId,
                    PlayerId = response.Joiner.PlayerId,
                    PlayerName = model.PlayerName,
                    IsPlayer1 = response.IsPlayer
                };
                _authDb.GameMemberships.Add(membership);
                await _authDb.SaveChangesAsync();
                return new JsonResult(new {
                    success = true,
                    playerId = membership.PlayerId
                });
            }
            return new JsonResult(new { success = false, message = "방 입장에 실패했습니다." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "방 입장 실패");
            return new JsonResult(new { success = false, message = ex.Message });
        }
    }
}

public class CreateRoomModel
{
    public string RoomName { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
}

public class JoinRoomModel
{
    public string RoomId { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
}