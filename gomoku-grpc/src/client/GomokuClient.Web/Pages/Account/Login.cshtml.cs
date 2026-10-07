using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace GomokuClient.Web.Pages.Account;

[AllowAnonymous]
public sealed class LoginModel(SignInManager<IdentityUser> signInManager) : PageModel
{
    [BindProperty] public InputModel Input { get; set; } = new();
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }

    public sealed class InputModel
    {
        [Required, EmailAddress] public string Email { get; set; } = string.Empty;
        [Required, DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
        public bool RememberMe { get; set; }
    }

    public void OnGet(string? returnUrl = null) => ReturnUrl = returnUrl;

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();

        var result = await signInManager.PasswordSignInAsync(
            Input.Email.Trim(), Input.Password, Input.RememberMe, lockoutOnFailure: true);

        if (result.RequiresTwoFactor)
            return RedirectToPage("/Account/LoginWith2fa", new { ReturnUrl, Input.RememberMe });
        if (result.Succeeded)
            return RedirectToLocal(ReturnUrl);

        ModelState.AddModelError(string.Empty, result.IsLockedOut
            ? "로그인 시도가 많아 계정이 잠겼습니다. 잠시 후 다시 시도하세요."
            : "이메일 또는 비밀번호를 확인하세요.");
        return Page();
    }

    private IActionResult RedirectToLocal(string? url) =>
        !string.IsNullOrWhiteSpace(url) && Url.IsLocalUrl(url) ? LocalRedirect(url) : RedirectToPage("/Index");
}
