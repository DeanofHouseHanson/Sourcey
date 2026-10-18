using Microsoft.AspNetCore.Identity;

namespace BuyList.Web.Security;

/// <summary>`dotnet run --project src/BuyList.Web -- hash-password` prints a value for Auth:PasswordHash.</summary>
public static class PasswordHashCommand
{
    public static int Run()
    {
        Console.Error.Write("Password: ");
        var pw = ReadHidden();
        Console.Error.Write("Repeat:   ");
        var again = ReadHidden();
        if (string.IsNullOrEmpty(pw) || pw != again)
        {
            Console.Error.WriteLine("Passwords empty or do not match.");
            return 1;
        }
        if (pw.Length < 12)
            Console.Error.WriteLine("Warning: fewer than 12 characters.");
        Console.WriteLine(new PasswordHasher<string>().HashPassword("user", pw));
        return 0;
    }

    private static string ReadHidden()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? string.Empty;
        var sb = new System.Text.StringBuilder();
        ConsoleKeyInfo k;
        while ((k = Console.ReadKey(true)).Key != ConsoleKey.Enter)
        {
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.Error.WriteLine();
        return sb.ToString();
    }
}
