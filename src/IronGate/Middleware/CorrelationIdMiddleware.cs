using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Microsoft.Extensions.Primitives;

namespace IronGate.Middleware;

public sealed class CorrelationIdMiddleware
{
    private const string CorrelationHeader = "x-correlation-id";
    private const string TraceParentHeader = "traceparent";
    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Extract or generate correlation ID
        string correlationId = context.Request.Headers.TryGetValue(CorrelationHeader, out StringValues headerValue)
            ? headerValue.ToString()
            : null;

        // If header is missing or empty, generate a new correlation ID
        if (string.IsNullOrEmpty(correlationId))
        {
            correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N")[..16];
        }

        // Set response header so clients can correlate
        context.Response.Headers[CorrelationHeader] = correlationId;

        // Add to logging scope
        using IDisposable? scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["correlationId"] = correlationId,
            ["traceId"] = Activity.Current?.TraceId.ToString() ?? "none",
            ["spanId"] = Activity.Current?.SpanId.ToString() ?? "none"
        });

        // Enrich activity with correlation ID
        if (Activity.Current != null)
        {
            Activity.Current.SetTag("correlation.id", correlationId);
        }

        _logger.LogDebug("Request started: {Method} {Path} CorrelationId={CorrelationId}",
            context.Request.Method, context.Request.Path, correlationId);

        try
        {
            await _next(context);
        }
        finally
        {
            _logger.LogDebug("Request completed: {Method} {Path} StatusCode={StatusCode} CorrelationId={CorrelationId}",
                context.Request.Method, context.Request.Path, context.Response.StatusCode, correlationId);
        }
    }
}

public static class CorrelationIdMiddlewareExtensions
{
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        return app.UseMiddleware<CorrelationIdMiddleware>();
    }
}