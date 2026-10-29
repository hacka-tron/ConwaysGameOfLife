using GameOfLife.Core;
using GameOfLife.Core.Storage;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace GameOfLife.Api;

/// <summary>Turns expected domain failures into problem details responses.</summary>
public sealed class BoardExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    // Non-standard code used by nginx and others for "client closed the request".
    private const int ClientClosedRequest = 499;

    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            // The client went away, so there is nobody to answer and nothing went wrong.
            context.Response.StatusCode = ClientClosedRequest;
            return true;
        }

        ProblemDetails? problem = exception switch
        {
            BoardNotFoundException e => new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Board not found",
                Detail = $"No board with ID '{e.BoardId}' exists.",
            },
            StepLimitExceededException e => new ProblemDetails
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "Board did not settle",
                Detail = $"The board did not reach a still life or a repeating cycle within {e.MaxSteps} generations.",
                Extensions = { ["maxSteps"] = e.MaxSteps },
            },
            // Thrown by Kestrel while reading the body, for example when it exceeds MaxRequestBodySize.
            BadHttpRequestException e => new ProblemDetails
            {
                Status = e.StatusCode,
                Title = e.StatusCode == StatusCodes.Status413PayloadTooLarge ? "Request too large" : "Bad request",
                Detail = e.Message,
            },
            StorageFullException => new ProblemDetails
            {
                Status = StatusCodes.Status507InsufficientStorage,
                Title = "Storage full",
                Detail = "The server has no room for more boards.",
            },
            _ => null,
        };

        if (problem is null)
        {
            return false;
        }

        context.Response.StatusCode = problem.Status!.Value;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}
