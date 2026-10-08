using GomokuClient.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using GomokuClient.Web.Services;
using GomokuGame.Proto;
using System.Net;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace GomokuClient.Web.Pages;

[Authorize]
public class GameModel : MfaRequiredPageModel
{
    private readonly GrpcGameClient _gameClient;
    private readonly ILogger<GameModel> _logger;

    [BindProperty(SupportsGet = true)]
    public string RoomId { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string PlayerName { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public bool IsPlayer1 { get; set; }

    [BindProperty(SupportsGet = true)]
    public string PlayerId { get; set; } = string.Empty;

    private readonly IConfiguration _config;
    private readonly AuthDbContext _authDb;
    private readonly IHttpClientFactory _httpClientFactory;

    public GameModel(GrpcGameClient gameClient, ILogger<GameModel> logger, IConfiguration config,
        UserManager<IdentityUser> userManager, AuthDbContext authDb, IHttpClientFactory httpClientFactory)
        : base(userManager)
    {
        _gameClient = gameClient;
        _logger = logger;
        _config = config;
        _authDb = authDb;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        if (string.IsNullOrWhiteSpace(RoomId)) return RedirectToPage("Index");
        var membership = await FindMembershipAsync(RoomId);
        if (membership is null) return NotFound();

        // URL에 실린 닉네임/참가자 정보 대신 로그인 사용자에게 저장된 값을 쓴다.
        PlayerName = membership.PlayerName;
        PlayerId = membership.PlayerId;
        IsPlayer1 = membership.IsPlayer1;
        return Page();
    }

    public async Task<IActionResult> OnGetGameState(string roomId)
    {
        if (await FindMembershipAsync(roomId) is null) return NotFound();
        try
        {
            var gameState = await _gameClient.GetGameStateAsync(roomId);
            return new JsonResult(new { gameState });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "게임 상태 조회 실패");
            return new JsonResult(new { gameState = (GameState?)null });
        }
    }

    public async Task<IActionResult> OnPostPlaceStone([FromBody] PlaceStoneModel model)
    {
        if (await FindMembershipAsync(model.RoomId) is not { } membership) return NotFound();
        try
        {
            // PlayerId는 요청 본문에서 받지 않고 소유권 DB의 값으로 덮어쓴다.
            var gameState = await _gameClient.PlaceStoneAsync(model.RoomId, membership.PlayerId, model.Row, model.Col);
            return new JsonResult(new {
                success = gameState != null,
                gameState,
                message = gameState != null ? "돌을 놓았습니다." : "돌을 놓을 수 없습니다."
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "돌 놓기 실패");
            return new JsonResult(new { success = false, message = "돌 놓기 중 오류가 발생했습니다." });
        }
    }

    public async Task<IActionResult> OnGetEventsAsync(string roomId, CancellationToken cancellationToken)
    {
        if (await FindMembershipAsync(roomId) is null) return NotFound();

        var baseUrl = (_config["GameServerUrl"] ?? "http://localhost:5224").TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/sse/game/{Uri.EscapeDataString(roomId)}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        using var response = await _httpClientFactory.CreateClient("game-backend").SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) return StatusCode((int)response.StatusCode);

        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache, no-store";
        Response.Headers["X-Accel-Buffering"] = "no";
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await stream.CopyToAsync(Response.Body, cancellationToken);
        return new EmptyResult();
    }

    private Task<GameMembership?> FindMembershipAsync(string roomId)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return Task.FromResult<GameMembership?>(null);
        return _authDb.GameMemberships.SingleOrDefaultAsync(
            membership => membership.UserId == userId && membership.RoomId == roomId);
    }

    public async Task<IActionResult> OnPostSurrender([FromBody] SurrenderModel model)
    {
        if (await FindMembershipAsync(model.RoomId) is not { } membership) return NotFound();
        try
        {
            var gameState = await _gameClient.SurrenderAsync(model.RoomId, membership.PlayerName);
            return new JsonResult(new {
                success = gameState != null,
                gameState
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "항복 실패");
            return new JsonResult(new { success = false });
        }
    }
}

public class PlaceStoneModel
{
    public string RoomId { get; set; } = string.Empty;
    public string PlayerId { get; set; } = string.Empty;
    public int Row { get; set; }
    public int Col { get; set; }
}

public class SurrenderModel
{
    public string RoomId { get; set; } = string.Empty;
    public string PlayerId { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
}