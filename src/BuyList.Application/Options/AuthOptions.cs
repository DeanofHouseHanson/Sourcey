namespace BuyList.Application.Options;

/// <summary>Single-user login. The hash is produced by <c>dotnet run --project src/BuyList.Web -- hash-password</c>.</summary>
public class AuthOptions
{
    public const string SectionName = "Auth";

    public string Username { get; set; } = "admin";

    /// <summary>ASP.NET Core Identity PasswordHasher output. Keep in user secrets / environment, not in source.</summary>
    public string? PasswordHash { get; set; }
}
