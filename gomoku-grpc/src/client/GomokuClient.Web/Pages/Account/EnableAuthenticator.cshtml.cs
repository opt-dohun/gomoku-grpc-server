using System.ComponentModel.DataAnnotations;
using GomokuClient.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GomokuClient.Web.Pages.Account;

[Authorize]
public sealed class EnableAuthenticatorModel(
    UserManager<IdentityUser> userManager,
    SignInManager<IdentityUser> signInManager) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    public string? QrCodeDataUri { get; private set; }
    public bool IsEnabled { get; private set; }
    public string ManualKey { get; private set; } = string.Empty;

    public sealed class InputModel
    {
        [Required, StringLength(10, MinimumLength = 6)]
        [RegularExpression("^[0-9 -]+$")]
        public string Code { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        Response.Headers.CacheControl = "no-store, no-cache";
        Response.Headers.Pragma = "no-cache";
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Challenge();

        IsEnabled = await userManager.GetTwoFactorEnabledAsync(user);
        if (IsEnabled) return Page();

        var key = await userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrWhiteSpace(key))
        {
            var reset = await userManager.ResetAuthenticatorKeyAsync(user);
            if (!reset.Succeeded) return StatusCode(StatusCodes.Status500InternalServerError);
            key = await userManager.GetAuthenticatorKeyAsync(user);
        }

        if (string.IsNullOrWhiteSpace(key)) return StatusCode(StatusCodes.Status500InternalServerError);
        ManualKey = FormatKey(key);
        var account = user.Email ?? user.UserName ?? user.Id;
        var uri = AuthenticatorUriBuilder.Build(account, key);
        QrCodeDataUri = QrCodeRenderer.ToPngDataUri(uri);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Response.Headers.CacheControl = "no-store, no-cache";
        if (!ModelState.IsValid) return await OnGetAsync();

        var user = await userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (await userManager.GetTwoFactorEnabledAsync(user))
        {
            ModelState.AddModelError(string.Empty, "2차 인증은 이미 활성화되어 있습니다.");
            return await OnGetAsync();
        }

        var code = Input.Code.Replace(" ", string.Empty).Replace("-", string.Empty);
        var valid = await userManager.VerifyTwoFactorTokenAsync(
            user, TokenOptions.DefaultAuthenticatorProvider, code);
        if (!valid)
        {
            // Identity 계정 잠금 카운터를 이용해 등록 단계 코드 추측도 제한한다.
            await userManager.AccessFailedAsync(user);
            ModelState.AddModelError(string.Empty, "인증 코드가 올바르지 않거나 만료되었습니다.");
            return await OnGetAsync();
        }

        await userManager.ResetAccessFailedCountAsync(user);
        var enabled = await userManager.SetTwoFactorEnabledAsync(user, true);
        if (!enabled.Succeeded)
        {
            ModelState.AddModelError(string.Empty, "2차 인증을 활성화하지 못했습니다. 다시 시도하세요.");
            return await OnGetAsync();
        }

        await signInManager.RefreshSignInAsync(user);
        return RedirectToPage("/Index");
    }

    private static string FormatKey(string key) => string.Join(" ",
        Enumerable.Range(0, (key.Length + 3) / 4).Select(index =>
            key.Substring(index * 4, Math.Min(4, key.Length - index * 4))));
}
