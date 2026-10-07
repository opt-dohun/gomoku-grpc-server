using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GomokuClient.Web.Security;

/// <summary>게임 페이지는 로그인뿐 아니라 등록 완료된 TOTP도 요구한다.</summary>
public abstract class MfaRequiredPageModel(UserManager<IdentityUser> userManager) : PageModel
{
    public override async Task OnPageHandlerExecutionAsync(
        PageHandlerExecutingContext context,
        PageHandlerExecutionDelegate next)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) { context.Result = Challenge(); return; }

        if (!await userManager.GetTwoFactorEnabledAsync(user))
        {
            context.Result = RedirectToPage("/Account/EnableAuthenticator");
            return;
        }

        await next();
    }
}
