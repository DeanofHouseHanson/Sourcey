using System.Security.Claims;
using BuyList.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace BuyList.Web.Pages.Account;

public class LoginModel : PageModel
{
    private readonly ISingleUserAuthenticator _auth;
    public LoginModel(ISingleUserAuthenticator auth) => _auth = auth;

    [BindProperty] public string? Username { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!_auth.Validate(Username, Password))
        {
            await Task.Delay(1000); // blunt brute-force brake
            Error = "Invalid username or password.";
            return Page();
        }

        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, Username!) }, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = true });

        return Url.IsLocalUrl(ReturnUrl) ? LocalRedirect(ReturnUrl!) : RedirectToPage("/Index");
    }
}
