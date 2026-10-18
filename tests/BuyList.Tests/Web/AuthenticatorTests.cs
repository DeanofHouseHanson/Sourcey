using BuyList.Application.Options;
using BuyList.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace BuyList.Tests.Web;

public class AuthenticatorTests
{
    private static SingleUserAuthenticator Make(string? hash, string user = "admin") =>
        new(Options.Create(new AuthOptions { Username = user, PasswordHash = hash }));

    private static string Hash(string pw) => new PasswordHasher<string>().HashPassword("admin", pw);

    [Fact]
    public void Correct_credentials_pass() => Assert.True(Make(Hash("s3cret-pass")).Validate("admin", "s3cret-pass"));

    [Fact]
    public void Wrong_password_fails() => Assert.False(Make(Hash("s3cret-pass")).Validate("admin", "nope"));

    [Fact]
    public void Wrong_username_fails() => Assert.False(Make(Hash("s3cret-pass")).Validate("root", "s3cret-pass"));

    [Fact]
    public void Missing_hash_never_authenticates() => Assert.False(Make(null).Validate("admin", ""));

    [Fact]
    public void Null_inputs_fail() => Assert.False(Make(Hash("x")).Validate(null, null));
}
