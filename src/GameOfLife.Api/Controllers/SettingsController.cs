using GameOfLife.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GameOfLife.Api.Controllers;

[ApiController]
[Route("settings")]
[Produces("application/json")]
public sealed class SettingsController(IOptions<GameOfLifeOptions> options) : ControllerBase
{
    /// <summary>
    /// Returns the server's limits and the edge rule given to uploads that do not name one.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<SettingsResponse>(StatusCodes.Status200OK)]
    public SettingsResponse Get()
    {
        var settings = options.Value;
        return new SettingsResponse(
            settings.MaxWidth,
            settings.MaxHeight,
            settings.MaxSteps,
            settings.DefaultEdgeMode.ToKey(),
            settings.MaxBoardsPerSecond,
            settings.MaxBatchSize);
    }
}
