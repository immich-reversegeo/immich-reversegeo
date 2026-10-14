using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ImmichReverseGeo.Web.Services;

public static class ProcessingEndpoints
{
    public static void MapProcessingEndpoints(this IEndpointRouteBuilder endpoints, Func<bool> tryTriggerRun)
    {
        endpoints.MapPost("/api/processing/run", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!tryTriggerRun())
            {
                return Results.Json(new { status = "busy", message = "Processing or maintenance is already in progress. No run was queued." },
                    statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Json(new { status = "accepted", message = "Processing accepted. Check the Dashboard or Logs for progress and outcome." },
                statusCode: StatusCodes.Status202Accepted);
        });
    }
}
