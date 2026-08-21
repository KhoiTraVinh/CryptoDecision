using System.Net;
using System.Text.Json;
using CryptoDecision.ApiService.Application;

namespace CryptoDecision.ApiService.Middleware;

/// <summary>
/// Catches unhandled exceptions and returns structured JSON errors.
/// Prevents stack traces from leaking to clients in production.
/// </summary>
public sealed class GlobalExceptionMiddleware(
    RequestDelegate next,
    ILogger<GlobalExceptionMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await next(ctx);
        }
        catch (RequestValidationException ex)
        {
            ctx.Response.StatusCode  = (int)HttpStatusCode.BadRequest;
            ctx.Response.ContentType = "application/json";
            var body = JsonSerializer.Serialize(new
            {
                type   = "validation_error",
                errors = new[] { new { PropertyName = ex.Field, ErrorMessage = ex.Message } }
            }, JsonOpts);
            await ctx.Response.WriteAsync(body);
        }
        catch (OperationCanceledException)
        {
            ctx.Response.StatusCode = 499;  // Client closed request
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception for {Method} {Path}",
                ctx.Request.Method, ctx.Request.Path);

            ctx.Response.StatusCode  = (int)HttpStatusCode.InternalServerError;
            ctx.Response.ContentType = "application/json";
            var body = JsonSerializer.Serialize(new
            {
                type    = "internal_error",
                message = "An unexpected error occurred"
            }, JsonOpts);
            await ctx.Response.WriteAsync(body);
        }
    }
}
