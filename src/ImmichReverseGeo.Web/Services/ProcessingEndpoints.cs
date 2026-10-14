using System;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ImmichReverseGeo.Web.Services;

public static class ProcessingEndpoints
{
    public static void MapProcessingEndpoints(this IEndpointRouteBuilder endpoints, string? apiKey, Func<bool> tryTriggerRun)
    {
        var keyHash = string.IsNullOrWhiteSpace(apiKey)
            ? null
            : SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));

        endpoints.MapPost("/api/processing/run", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (keyHash is null)
            {
                return Results.Json(new { status = "disabled", message = "Configure RUN_API_KEY to enable API runs." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!HasValidApiKey(context.Request, keyHash))
            {
                context.Response.Headers.WWWAuthenticate = "Bearer";
                return Results.Json(new { status = "unauthorized", message = "A valid Bearer API key is required." },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!tryTriggerRun())
            {
                return Results.Json(new { status = "busy", message = "Processing or maintenance is already in progress. No run was queued." },
                    statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Json(new { status = "accepted", message = "Processing accepted. Check the Dashboard or Logs for progress and outcome." },
                statusCode: StatusCodes.Status202Accepted);
        });
    }

    private static bool HasValidApiKey(HttpRequest request, byte[] expectedHash)
    {
        if (!AuthenticationHeaderValue.TryParse(request.Headers.Authorization.ToString(), out var authorization)
            || !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(authorization.Parameter))
        {
            return false;
        }

        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(authorization.Parameter));
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }
}
