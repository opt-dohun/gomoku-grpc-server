namespace GomokuClient.Web.Security;

/// <summary>웹 로그인 사용자와 게임 서버 참가 ID의 관계를 로컬 DB에 보관한다.</summary>
public sealed class GameMembership
{
    public string UserId { get; set; } = string.Empty;
    public string RoomId { get; set; } = string.Empty;
    public string PlayerId { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public bool IsPlayer1 { get; set; }
}
