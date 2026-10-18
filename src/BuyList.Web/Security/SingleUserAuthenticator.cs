using System.Security.Cryptography;
using System.Text;
using BuyList.Application.Options;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace BuyList.Web.Security;

public interface ISingleUserAuthenticator
{
    bool Validate(string? username, string? password);
}

/// <summary>Validates the one configured user against a hashed password; there is no registration.</summary>
public class SingleUserAuthenticator : ISingleUserAuthenticator
{
    private readonly PasswordHasher<string> _hasher = new();
    private readonly AuthOptions _options;
    // Verified when the username is wrong, so timing doesn't reveal which half failed.
    private readonly string _dummyHash;

    public SingleUserAuthenticator(IOptions<AuthOptions> options)
    {
        _options = options.Value;
        _dummyHash = _hasher.HashPassword("x", Guid.NewGuid().ToString());
    }

    public bool Validate(string? username, string? password)
    {
        var userOk = CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(username ?? string.Empty)),
            SHA256.HashData(Encoding.UTF8.GetBytes(_options.Username)));

        var hash = userOk && !string.IsNullOrEmpty(_options.PasswordHash) ? _options.PasswordHash : _dummyHash;
        var result = _hasher.VerifyHashedPassword(_options.Username, hash, password ?? string.Empty);
        return userOk && !ReferenceEquals(hash, _dummyHash) && result != PasswordVerificationResult.Failed;
    }
}
