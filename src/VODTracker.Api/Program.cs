using VODTracker.Contracts;
using VODTracker.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var databasePath = builder.Configuration["VODTracker:DatabasePath"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VODTracker", "vodtracker.db");
builder.Services.AddSingleton(new SqliteDatabase(databasePath));

var app = builder.Build();
await app.Services.GetRequiredService<SqliteDatabase>().InitializeAsync();
app.MapGet("/api/v1/health", () => Results.Ok(new HealthResponse("ok", "1.0.0", DateTimeOffset.UtcNow)));
app.Run();

public partial class Program { }
