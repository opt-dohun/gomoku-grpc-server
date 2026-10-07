using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GomokuClient.Web.Pages.Account;

[AllowAnonymous]
public sealed class LoginWith2faModel(SignInManager<IdentityUser> signInManager) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    [BindProperty(SupportsGet = true)] public bool RememberMe { get; set; }

    public sealed class InputModel
    {
        [Required, StringLength(10, MinimumLength = 6)]
        [RegularExpression("^[0-9 -]+$")]
        public string Code { get; set; } = string.Empty;
    }

    public async Task<IActionResult> OnGetAsync(string? returnUrl = null, bool rememberMe = false)
    {
        // Identity의 임시 2FA 쿠키가 없으면 인증 단계 URL 직접 접근을 거부한다.
        if (await signInManager.GetTwoFactorAuthenticationUserAsync() is null)
            return RedirectToPage("/Account/Login");

        ReturnUrl = returnUrl;
        RememberMe = rememberMe;
        Response.Headers.CacheControl = "no-store, no-cache";
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();

        var code = Input.Code.Replace(" ", string.Empty).Replace("-", string.Empty);
        var result = await signInManager.TwoFactorAuthenticatorSignInAsync(
            code, RememberMe, rememberClient: false);

        if (result.Succeeded)
            return !string.IsNullOrWhiteSpace(ReturnUrl) && Url.IsLocalUrl(ReturnUrl)
                ? LocalRedirect(ReturnUrl)
                : RedirectToPage("/Index");
        if (result.IsLockedOut)
            return RedirectToPage("/Account/Lockout");

        ModelState.AddModelError(string.Empty, "인증 코드가 올바르지 않거나 만료되었습니다.");
        return Page();
    }
}
