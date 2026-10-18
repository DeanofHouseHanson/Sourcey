using BuyList.Application;
using BuyList.Application.Options;
using BuyList.Infrastructure;
using BuyList.Infrastructure.Persistence;
using BuyList.Web.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

if (args.Length > 0 && args[0] == "hash-password")
    return PasswordHashCommand.Run();

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddOptions<AuthOptions>().Bind(config.GetSection(AuthOptions.SectionName));
builder.Services.AddOptions<KeepaOptions>().Bind(config.GetSection(KeepaOptions.SectionName));
builder.Services.AddOptions<ProfitOptions>().Bind(config.GetSection(ProfitOptions.SectionName));
builder.Services.AddOptions<RiskOptions>().Bind(config.GetSection(RiskOptions.SectionName));
builder.Services.AddOptions<RankingOptions>().Bind(config.GetSection(RankingOptions.SectionName));
builder.Services.AddOptions<FbaFeeOptions>().Bind(config.GetSection(FbaFeeOptions.SectionName));

builder.Services.AddApplication();

builder.Services.AddInfrastructure(config);
builder.Services.AddSingleton<ISingleUserAuthenticator, SingleUserAuthenticator>();

// Without a password hash nobody can log in. Fail fast outside Development rather than run unprotected.
if (string.IsNullOrWhiteSpace(config[$"{AuthOptions.SectionName}:PasswordHash"]) && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException(
        "Auth:PasswordHash is not configured. Generate one with 'dotnet run --project src/BuyList.Web -- hash-password' " +
        "and supply it as the Auth__PasswordHash environment variable.");

// Development convenience only: with no hash configured, the password is "changeme" (a warning is logged at startup).
const string DevFallbackPassword = "changeme";
var devFallbackActive = builder.Environment.IsDevelopment()
    && string.IsNullOrWhiteSpace(config[$"{AuthOptions.SectionName}:PasswordHash"]);
if (devFallbackActive)
    builder.Services.PostConfigure<AuthOptions>(o =>
        o.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<string>().HashPassword(o.Username, DevFallbackPassword));

var keysPath = config["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath))
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysPath)).SetApplicationName("BuyList");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Account/Login";
        o.AccessDeniedPath = "/Account/Login";
        o.Cookie.Name = "buylist.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // TLS is usually terminated by the reverse proxy
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();

builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/");            // everything requires login...
    o.Conventions.AllowAnonymousToPage("/Account/Login"); // ...except the login page
    o.Conventions.AllowAnonymousToPage("/Error");
});

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BuyListDbContext>();
    db.Database.Migrate();
}

if (devFallbackActive)
    app.Logger.LogWarning("Auth:PasswordHash is not set; using the development password '{Password}'. Set a real hash via user secrets.", DevFallbackPassword);

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

app.Run();
return 0;

public partial class Program { }
