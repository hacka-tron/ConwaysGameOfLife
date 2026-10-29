using GameOfLife.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace GameOfLife.Api.Controllers;

[ApiController]
[Route("boards")]
[Produces("application/json")]
public sealed class BoardsController(BoardService boards, IOptions<GameOfLifeOptions> options) : ControllerBase
{
    /// <summary>Stores a board and returns its ID. Uploading the same board again returns the same ID.</summary>
    /// <response code="201">The board is new.</response>
    /// <response code="200">The board was already stored.</response>
    /// <response code="400">The grid is empty, ragged, too large, or has values other than 0 and 1.</response>
    [HttpPost]
    [ProducesResponseType<BoardResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<BoardResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<BoardResponse>> Upload(UploadBoardRequest request, CancellationToken cancellationToken)
    {
        // A single | runs both checks, so one response reports every problem with the request.
        if (!BoardRequestValidator.TryParse(request.Cells, options.Value, ModelState, out var cells)
            | !BoardRequestValidator.TryParseEdgeMode(request.EdgeMode, options.Value.DefaultEdgeMode, ModelState, out var edgeMode))
        {
            return ValidationProblem(ModelState);
        }

        var (board, created) = await boards.UploadAsync(cells, edgeMode, cancellationToken);
        var response = BoardResponse.From(board);
        return created ? CreatedAtAction(nameof(Get), new { id = board.Id }, response) : Ok(response);
    }

    /// <summary>Returns a stored board.</summary>
    [HttpGet("{id}")]
    [ProducesResponseType<BoardResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<BoardResponse> Get(string id, CancellationToken cancellationToken) =>
        BoardResponse.From(await boards.GetAsync(id, cancellationToken));

    /// <summary>Returns the next generation of a board, with its own ID.</summary>
    [HttpGet("{id}/next")]
    [ProducesResponseType<BoardResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<BoardResponse> Next(string id, CancellationToken cancellationToken) =>
        BoardResponse.From(await boards.GetNextAsync(id, cancellationToken));

    /// <summary>Returns the board after the given number of generations, with its own ID.</summary>
    /// <remarks>
    /// Any number of generations works once the board settles into a still life or cycle within the
    /// configured step limit. Otherwise the request fails with 422.
    /// </remarks>
    [HttpGet("{id}/generations/{generations}")]
    [ProducesResponseType<BoardResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<BoardResponse>> Generation(string id, long generations, CancellationToken cancellationToken)
    {
        if (generations < 0)
        {
            ModelState.AddModelError(nameof(generations), "Generations cannot be negative.");
            return ValidationProblem(ModelState);
        }

        return BoardResponse.From(await boards.GetGenerationAsync(id, generations, cancellationToken));
    }

    /// <summary>
    /// Returns the state the board settles into: a still life, or the canonical member of the cycle it repeats.
    /// </summary>
    /// <response code="422">The board did not settle within the configured step limit.</response>
    [HttpGet("{id}/final")]
    [ProducesResponseType<FinalStateResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<FinalStateResponse> Final(string id, CancellationToken cancellationToken) =>
        FinalStateResponse.From(await boards.GetFinalAsync(id, cancellationToken));
}
