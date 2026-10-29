using System.Net;
using System.Net.Http.Json;
using GameOfLife.Api;
using Microsoft.AspNetCore.Mvc;

namespace GameOfLife.Tests.Integration;

public sealed class BoardsApiTests : IDisposable
{
    private readonly TempStorage _storage = new();
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public BoardsApiTests()
    {
        _factory = new ApiFactory(_storage.Path);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _storage.Dispose();
    }

    [Fact]
    public async Task UploadReturnsCreatedThenOkWithTheSameId()
    {
        var first = await _client.PostAsJsonAsync("/boards", Upload(Patterns.Block));
        var second = await _client.PostAsJsonAsync("/boards", Upload(Patterns.Block));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var created = await Read<BoardResponse>(first);
        Assert.Equal(created.Id, (await Read<BoardResponse>(second)).Id);
        Assert.Equal($"/boards/{created.Id}", first.Headers.Location!.AbsolutePath);
    }

    public static TheoryData<string, string> InvalidBoards => new()
    {
        { """{"cells":[]}""", "at least one row" },
        { """{}""", "at least one row" },
        { """{"cells":[[]]}""", "at least one cell" },
        { """{"cells":[[0,1],[1]]}""", "same number of cells" },
        { """{"cells":[[0,2]]}""", "must be 0 or 1" },
        { """{"cells":[null]}""", "cannot be null" },
        { $$"""{"cells":[[{{string.Join(",", Enumerable.Repeat(0, 257))}}]]}""", "maximum is 256" },
    };

    [Theory]
    [MemberData(nameof(InvalidBoards))]
    public async Task UploadRejectsInvalidBoards(string json, string expectedMessage)
    {
        var response = await _client.PostAsync("/boards", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expectedMessage, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NextReturnsTheNextGenerationWithItsOwnId()
    {
        var horizontal = await UploadAsync(Patterns.BlinkerHorizontal);

        var vertical = await _client.GetFromJsonAsync<BoardResponse>($"/boards/{horizontal.Id}/next");
        var backAgain = await _client.GetFromJsonAsync<BoardResponse>($"/boards/{vertical!.Id}/next");

        Assert.Equal(Patterns.ToGrid(Patterns.BlinkerVertical), vertical.Cells);
        Assert.Equal(horizontal.Id, backAgain!.Id);
    }

    [Fact]
    public async Task GenerationReturnsBoardAfterNSteps()
    {
        var glider = await UploadAsync(Patterns.GliderOn(10));

        var afterFour = await _client.GetFromJsonAsync<BoardResponse>($"/boards/{glider.Id}/generations/4");
        var viaNext = glider;
        for (int i = 0; i < 4; i++)
        {
            viaNext = (await _client.GetFromJsonAsync<BoardResponse>($"/boards/{viaNext.Id}/next"))!;
        }

        Assert.Equal(viaNext.Id, afterFour!.Id);
        // The returned ID can be used directly in later requests.
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/boards/{afterFour.Id}")).StatusCode);
    }

    [Fact]
    public async Task GenerationHandlesHugeNumbersOnceTheBoardSettles()
    {
        var blinker = await UploadAsync(Patterns.BlinkerHorizontal);

        var even = await _client.GetFromJsonAsync<BoardResponse>($"/boards/{blinker.Id}/generations/9000000000000000000");
        await _client.GetAsync($"/boards/{blinker.Id}/final");
        var odd = await _client.GetFromJsonAsync<BoardResponse>($"/boards/{blinker.Id}/generations/9000000000000000001");

        Assert.Equal(blinker.Id, even!.Id);
        Assert.Equal(Patterns.ToGrid(Patterns.BlinkerVertical), odd!.Cells);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("abc")]
    public async Task GenerationRejectsInvalidCounts(string generations)
    {
        var block = await UploadAsync(Patterns.Block);

        var response = await _client.GetAsync($"/boards/{block.Id}/generations/{generations}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FinalReturnsTheCycleTheBoardSettlesInto()
    {
        var blinker = await UploadAsync(Patterns.BlinkerVertical);

        var final = await _client.GetFromJsonAsync<FinalStateResponse>($"/boards/{blinker.Id}/final");
        var again = await _client.GetFromJsonAsync<FinalStateResponse>($"/boards/{blinker.Id}/final");

        Assert.Equal((2, "oscillator"), (final!.Period, final.Kind));
        Assert.Equal((final.Board.Id, final.Steps, final.Period), (again!.Board.Id, again.Steps, again.Period));
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"/boards/{final.Board.Id}")).StatusCode);
    }

    [Fact]
    public async Task FinalReportsAStillLife()
    {
        var block = await UploadAsync(Patterns.Block);

        var final = await _client.GetFromJsonAsync<FinalStateResponse>($"/boards/{block.Id}/final");

        Assert.Equal((block.Id, 0, 1, "still-life"), (final!.Board.Id, final.Steps, final.Period, final.Kind));
    }

    [Fact]
    public async Task UnknownBoardReturnsNotFound()
    {
        foreach (string path in new[] { "", "/next", "/generations/3", "/final" })
        {
            var response = await _client.GetAsync($"/boards/does-not-exist{path}");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("Board not found", (await Read<ProblemDetails>(response)).Title);
        }
    }

    [Fact]
    public async Task HealthCheckReportsHealthy()
    {
        Assert.Equal("Healthy", await _client.GetStringAsync("/health"));
    }

    [Fact]
    public async Task BrowserUiIsServedWithoutStaleCaching()
    {
        var response = await _client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoCache);
        Assert.Contains("id=\"load-form\"", await response.Content.ReadAsStringAsync());
    }

    private async Task<BoardResponse> UploadAsync(string[] rows)
    {
        var response = await _client.PostAsJsonAsync("/boards", Upload(rows));
        response.EnsureSuccessStatusCode();
        return await Read<BoardResponse>(response);
    }

    private static UploadBoardRequest Upload(string[] rows) => new() { Cells = Patterns.ToGrid(rows) };

    private static async Task<T> Read<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>())!;
}
