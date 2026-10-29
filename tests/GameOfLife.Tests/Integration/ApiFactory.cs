using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GameOfLife.Tests.Integration;

/// <summary>Runs the API in memory against its own storage folder.</summary>
public sealed class ApiFactory(string storagePath, IReadOnlyDictionary<string, string>? settings = null)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Storage:Path", storagePath);
        builder.UseSetting("Storage:MaxSizeBytes", (16 * 1024 * 1024).ToString());
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            builder.UseSetting(key, value);
        }
    }
}

/// <summary>A temporary storage folder, deleted when the test finishes.</summary>
public sealed class TempStorage : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gol-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
