using System.Net;
using System.Net.Http.Json;
using GameOfLife.Api;
using GameOfLife.Core;
using GameOfLife.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace GameOfLife.Tests.Integration;

/// <summary>Batches of consecutive generations for playback, and the per-client playback rate limit.</summary>
public sealed class SequenceTests : IDisposable
{
    private readonly TempStorage _storage = new();
    private readonly LifeSimulator _simulator = new(new LifeEngine());

    public void Dispose() => _storage.Dispose();

    [Fact]
    public async Task ReturnsConsecutiveGenerationsThatMatchNext()
    {
        using var factory = new ApiFactory(_storage.Path);
        var client = factory.CreateClient();
        var start = Patterns.Parse(Patterns.GliderOn(20));
        string id = await UploadAsync(client, start);

        var sequence = await GetSequenceAsync(client, id, 10);

        Assert.Null(sequence.RepeatsFrom);
        Assert.Equal(
            Enumerable.Range(1, 10).Select(g => _simulator.Advance(start, g, CancellationToken.None).Id),
            sequence.Boards.Select(b => b.Id));
        // Every returned ID can be used in later requests.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/boards/{sequence.Boards[^1].Id}")).StatusCode);
    }

    [Fact]
    public async Task SavesTheNextLinksItComputes()
    {
        using var factory = new ApiFactory(_storage.Path);
        var client = factory.CreateClient();
        string id = await UploadAsync(client, Patterns.Parse(Patterns.GliderOn(20)));

        var sequence = await GetSequenceAsync(client, id, 5);

        var store = factory.Services.GetRequiredService<IBoardStore>();
        var ids = new[] { id }.Concat(sequence.Boards.Select(b => b.Id)).ToList();
        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(ids[i + 1], (await store.GetAsync(ids[i], CancellationToken.None))!.NextId);
        }

        // The saved chain reads back in one go, up to the length asked for or the last saved link.
        Assert.Equal(ids.Skip(1).Take(3), (await store.GetChainAsync(id, 3, CancellationToken.None)).Select(b => b.Id));
        Assert.Equal(ids.Skip(1), (await store.GetChainAsync(id, 50, CancellationToken.None)).Select(b => b.Id));
    }

    [Fact]
    public async Task SavedChainAroundACycleStopsAtTheLengthAskedFor()
    {
        using var factory = new ApiFactory(_storage.Path);
        var client = factory.CreateClient();
        var horizontal = Patterns.Parse(Patterns.BlinkerHorizontal);
        var vertical = Patterns.Parse(Patterns.BlinkerVertical);
        await GetSequenceAsync(client, await UploadAsync(client, horizontal), 10);

        var chain = await factory.Services.GetRequiredService<IBoardStore>().GetChainAsync(horizontal.Id, 5, CancellationToken.None);

        Assert.Equal([vertical.Id, horizontal.Id, vertical.Id, horizontal.Id, vertical.Id], chain.Select(b => b.Id));
    }

    [Fact]
    public async Task ContinuesFromSavedLinksIntoNewGenerations()
    {
        using var factory = new ApiFactory(_storage.Path);
        var client = factory.CreateClient();
        var start = Patterns.Parse(Patterns.GliderOn(20));
        string id = await UploadAsync(client, start);
        await GetSequenceAsync(client, id, 3);

        var longer = await GetSequenceAsync(client, id, 8);

        Assert.Equal(
            Enumerable.Range(1, 8).Select(g => _simulator.Advance(start, g, CancellationToken.None).Id),
            longer.Boards.Select(b => b.Id));
    }

    [Fact]
    public async Task StillLifeLoopsToItselfWithNoBoards()
    {
        using var factory = new ApiFactory(_storage.Path);
        var client = factory.CreateClient();
        string id = await UploadAsync(client, Patterns.Parse(Patterns.Block));

        var sequence = await GetSequenceAsync(client, id, 10);

        Assert.Empty(sequence.Boards);
        Assert.Equal(0, sequence.RepeatsFrom);
    }

    [Fact]
    public async Task OscillatorStopsAtTheEndOfItsCycle()
    {
        using var factory = new ApiFactory(_storage.Path);
        var client = factory.CreateClient();
        string id = await UploadAsync(client, Patterns.Parse(Patterns.BlinkerHorizontal));

        var sequence = await GetSequenceAsync(client, id, 10);

        Assert.Equal([Patterns.ToGrid(Patterns.BlinkerVertical)], sequence.Boards.Select(b => b.Cells));
        Assert.Equal(0, sequence.RepeatsFrom);
    }

    [Fact]
    public async Task SettlingBoardLoopsToItsLastGeneration()
    {
        using var factory = new ApiFactory(_storage.Path);
        var client = factory.CreateClient();
        var start = Patterns.Parse(Patterns.GliderOn(8));
        var final = (await _simulator.RunAsync(start, null, 1000, null, CancellationToken.None))!.Final!;
        string id = await UploadAsync(client, start);

        var sequence = await GetSequenceAsync(client, id, 100);

        Assert.Equal(final.Steps, sequence.Boards.Length);
        Assert.Equal(final.Board.Id, sequence.Boards[^1].Id);
        Assert.Equal(final.Steps, sequence.RepeatsFrom);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("121")]
    [InlineData("abc")]
    public async Task RejectsCountsOutsideTheBatchLimit(string count)
    {
        using var factory = new ApiFactory(_storage.Path);
        var client = factory.CreateClient();
        string id = await UploadAsync(client, Patterns.Parse(Patterns.Block));

        var response = await client.GetAsync($"/boards/{id}/sequence?count={count}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownBoardReturnsNotFound()
    {
        using var factory = new ApiFactory(_storage.Path);

        var response = await factory.CreateClient().GetAsync("/boards/does-not-exist/sequence?count=5");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PlaybackIsRateLimitedInBoardsPerClient()
    {
        // A burst allowance of 2 x 5 boards, refilled at 1 board per second.
        using var factory = new ApiFactory(_storage.Path, new Dictionary<string, string>
        {
            ["GameOfLife:MaxBoardsPerSecond"] = "1",
            ["GameOfLife:MaxBatchSize"] = "5",
        });
        var client = factory.CreateClient();
        string id = await UploadAsync(client, Patterns.Parse(Patterns.GliderOn(20)));

        var first = await client.GetAsync($"/boards/{id}/sequence?count=5");
        var second = await client.GetAsync($"/boards/{id}/sequence?count=4");
        var next = await client.GetAsync($"/boards/{id}/next");
        var limited = await client.GetAsync($"/boards/{id}/next");

        Assert.Equal(
            [HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests],
            new[] { first.StatusCode, second.StatusCode, next.StatusCode, limited.StatusCode });
        Assert.NotNull(limited.Headers.RetryAfter);
        var problem = await limited.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("Too many boards requested", problem!.Title);
        // Other operations are not part of the playback limit.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/boards/{id}/generations/3")).StatusCode);
    }

    private static async Task<string> UploadAsync(HttpClient client, Board board)
    {
        var response = await client.PostAsJsonAsync("/boards", new UploadBoardRequest { Cells = Patterns.ToGrid(Patterns.ToText(board)) });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BoardResponse>())!.Id;
    }

    private static async Task<BoardSequenceResponse> GetSequenceAsync(HttpClient client, string id, int count) =>
        (await client.GetFromJsonAsync<BoardSequenceResponse>($"/boards/{id}/sequence?count={count}"))!;
}
