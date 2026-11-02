using System.Net;
using System.Net.Http.Json;
using GameOfLife.Api;
using GameOfLife.Core;
using GameOfLife.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace GameOfLife.Tests.Integration;

/// <summary>Final states are saved for every board a run passes through, not just the one asked about.</summary>
public sealed class MemoizationTests : IDisposable
{
    private readonly TempStorage _storage = new();
    private readonly LifeSimulator _simulator = new(new LifeEngine());

    public void Dispose() => _storage.Dispose();

    [Fact]
    public async Task FinalIsSavedForEveryBoardOnTheWay()
    {
        using var factory = new ApiFactory(_storage.Path);
        var client = factory.CreateClient();
        var start = Patterns.Parse(Patterns.GliderOn(12));

        var final = await GetFinalAsync(client, await UploadAsync(client, start));

        var store = factory.Services.GetRequiredService<IBoardStore>();
        for (int generation = 0; generation <= final.Steps; generation++)
        {
            string id = _simulator.Advance(start, generation, CancellationToken.None).Id;
            var saved = await store.GetFinalAsync(id, CancellationToken.None);
            Assert.Equal(new StoredFinalState(final.Board.Id, final.Steps - generation, final.Period), saved);
        }
    }

    [Fact]
    public async Task FinalIsSavedForEveryMemberOfTheCycle()
    {
        using var factory = new ApiFactory(_storage.Path);
        var client = factory.CreateClient();
        var horizontal = Patterns.Parse(Patterns.BlinkerHorizontal);
        var vertical = Patterns.Parse(Patterns.BlinkerVertical);

        var final = await GetFinalAsync(client, await UploadAsync(client, horizontal));

        var store = factory.Services.GetRequiredService<IBoardStore>();
        var other = final.Board.Id == horizontal.Id ? vertical : horizontal;
        Assert.Equal(new StoredFinalState(final.Board.Id, 1, 2), await store.GetFinalAsync(other.Id, CancellationToken.None));
        Assert.Equal(new StoredFinalState(final.Board.Id, 0, 2), await store.GetFinalAsync(final.Board.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ABoardMidwayAlongAKnownPathReportsStepsFromItself()
    {
        using var factory = new ApiFactory(_storage.Path);
        var client = factory.CreateClient();
        var start = Patterns.Parse(Patterns.GliderOn(12));
        var final = await GetFinalAsync(client, await UploadAsync(client, start));

        var midway = _simulator.Advance(start, 7, CancellationToken.None);
        var fromMidway = await GetFinalAsync(client, await UploadAsync(client, midway));

        Assert.Equal((final.Board.Id, final.Steps - 7, final.Period), (fromMidway.Board.Id, fromMidway.Steps, fromMidway.Period));
    }

    [Fact]
    public async Task ALaterRunStopsWhereItJoinsAKnownPath()
    {
        // With a limit of 10 generations, the earlier board can only be answered by reusing the later one's path.
        using var factory = new ApiFactory(_storage.Path, new Dictionary<string, string> { ["GameOfLife:MaxSteps"] = "10" });
        var client = factory.CreateClient();
        var start = Patterns.Parse(Patterns.GliderOn(12));
        int steps = (await _simulator.RunAsync(start, null, 1000, null, CancellationToken.None))!.Final!.Steps;
        Assert.True(steps > 20);
        var late = _simulator.Advance(start, steps - 8, CancellationToken.None);
        var early = _simulator.Advance(start, steps - 16, CancellationToken.None);

        var tooFar = await client.GetAsync($"/boards/{await UploadAsync(client, early)}/final");
        await GetFinalAsync(client, await UploadAsync(client, late));
        var fromEarly = await GetFinalAsync(client, early.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooFar.StatusCode);
        Assert.Equal(16, fromEarly.Steps);
    }

    private static async Task<string> UploadAsync(HttpClient client, Board board)
    {
        var response = await client.PostAsJsonAsync("/boards", new UploadBoardRequest { Cells = Patterns.ToGrid(Patterns.ToText(board)) });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BoardResponse>())!.Id;
    }

    private static async Task<FinalStateResponse> GetFinalAsync(HttpClient client, string id) =>
        (await client.GetFromJsonAsync<FinalStateResponse>($"/boards/{id}/final"))!;
}
