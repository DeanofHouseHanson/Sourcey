using BuyList.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BuyList.Tests;

/// <summary>A migrated in-memory SQLite database; each instance is isolated.</summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");

    public TestDb()
    {
        _conn.Open();
        using var db = NewContext();
        db.Database.Migrate();
    }

    public BuyListDbContext NewContext() =>
        new(new DbContextOptionsBuilder<BuyListDbContext>().UseSqlite(_conn).Options);

    public void Dispose() => _conn.Dispose();
}

public sealed class FixedTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
