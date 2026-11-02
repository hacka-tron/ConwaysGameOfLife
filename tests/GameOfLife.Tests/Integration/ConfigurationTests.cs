using System.Net;
using System.Net.Http.Json;
using GameOfLife.Api;
using Microsoft.AspNetCore.Mvc;

namespace GameOfLife.Tests.Integration;

public sealed class ConfigurationTests : IDisposable
{
    private readonly TempStorage _storage = new();

    public void Dispose() => _storage.Dispose();

    [Fact]
    public async Task BoardsSurviveARestart()
    {
        BoardResponse uploaded;
        using (var firstRun = new ApiFactory(_storage.Path))
        {
            var response = await firstRun.CreateClient().PostAsJsonAsync("/boards", Upload(Patterns.Block));
            uploaded = (await response.Content.ReadFromJsonAsync<BoardResponse>())!;
        }

        using var secondRun = new ApiFactory(_storage.Path);
        var reloaded = await secondRun.CreateClient().GetFromJsonAsync<BoardResponse>($"/boards/{uploaded.Id}");

        Assert.Equal(uploaded.Id, reloaded!.Id);
        Assert.Equal(uploaded.Cells, reloaded.Cells);
    }

    [Fact]
    public async Task MaxBoardSizeComesFromSettings()
    {
        using var factory = new ApiFactory(_storage.Path, new Dictionary<string, string>
        {
            ["GameOfLife:MaxWidth"] = "4",
            ["GameOfLife:MaxHeight"] = "4",
        });
        var client = factory.CreateClient();

        var fits = await client.PostAsJsonAsync("/boards", Upload(Patterns.Block));
        var tooBig = await client.PostAsJsonAsync("/boards", Upload(Patterns.BlinkerHorizontal));

        Assert.Equal(HttpStatusCode.Created, fits.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);
        Assert.Contains("maximum is 4", await tooBig.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task StepLimitComesFromSettings()
    {
        using var factory = new ApiFactory(_storage.Path, new Dictionary<string, string>
        {
            ["GameOfLife:MaxSteps"] = "5",
        });
        var client = factory.CreateClient();
        var glider = await (await client.PostAsJsonAsync("/boards", Upload(Patterns.GliderOn(20))))
            .Content.ReadFromJsonAsync<BoardResponse>();

        var farAhead = await client.GetAsync($"/boards/{glider!.Id}/generations/11");
        var final = await client.GetAsync($"/boards/{glider.Id}/final");
        var withinLimit = await client.GetAsync($"/boards/{glider.Id}/generations/5");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, farAhead.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, final.StatusCode);
        var problem = await final.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Contains("within 5 generations", problem!.Detail);
        Assert.Equal(HttpStatusCode.OK, withinLimit.StatusCode);
    }

    [Fact]
    public async Task ARunThatHitsTheStepLimitSavesItsProgressForLaterRequests()
    {
        using var factory = new ApiFactory(_storage.Path, new Dictionary<string, string>
        {
            ["GameOfLife:MaxSteps"] = "5",
        });
        var client = factory.CreateClient();
        var glider = await (await client.PostAsJsonAsync("/boards", Upload(Patterns.GliderOn(20))))
            .Content.ReadFromJsonAsync<BoardResponse>();

        // The first request computes 5 generations and saves them. The second walks those 5 and computes 3 more.
        var first = await client.GetAsync($"/boards/{glider!.Id}/generations/8");
        var second = await client.GetAsync($"/boards/{glider.Id}/generations/8");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var simulator = new GameOfLife.Core.LifeSimulator(new GameOfLife.Core.LifeEngine());
        var expected = simulator.Advance(Patterns.Parse(Patterns.GliderOn(20)), 8, CancellationToken.None);
        Assert.Equal(expected.Id, (await second.Content.ReadFromJsonAsync<BoardResponse>())!.Id);
    }

    [Fact]
    public async Task DefaultEdgeModeComesFromSettings()
    {
        using var factory = new ApiFactory(_storage.Path, new Dictionary<string, string>
        {
            ["GameOfLife:DefaultEdgeMode"] = "Wrap",
        });
        var client = factory.CreateClient();

        var board = await (await client.PostAsJsonAsync("/boards", Upload(Patterns.Block)))
            .Content.ReadFromJsonAsync<BoardResponse>();

        Assert.Equal("wrap", board!.EdgeMode);
    }

    [Fact]
    public async Task SettingsEndpointReportsConfiguredValues()
    {
        using var factory = new ApiFactory(_storage.Path, new Dictionary<string, string>
        {
            ["GameOfLife:MaxWidth"] = "64",
            ["GameOfLife:MaxSteps"] = "500",
            ["GameOfLife:DefaultEdgeMode"] = "Wrap",
        });

        var settings = await factory.CreateClient().GetFromJsonAsync<SettingsResponse>("/settings");

        Assert.Equal(new SettingsResponse(64, 256, 500, "wrap", 30, 120), settings);
    }

    [Fact]
    public void InvalidSettingsStopTheAppFromStarting()
    {
        using var factory = new ApiFactory(_storage.Path, new Dictionary<string, string>
        {
            ["GameOfLife:MaxSteps"] = "0",
        });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("MaxSteps", error.Message);
    }

    private static UploadBoardRequest Upload(string[] rows) => new() { Cells = Patterns.ToGrid(rows) };
}
