using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using IronGate.Domain;

namespace IronGate.Middleware;

public sealed class LoadSheddingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<LoadSheddingMiddleware> _logger;
    private readonly LoadSheddingOptions _options;
    private readonly Counter<long> _rejectedCounter;
    private readonly UpDownCounter<long> _inFlightCounter;
    private long _currentInFlight = 0;

    public LoadSheddingMiddleware(
        RequestDelegate next,
        ILogger<LoadSheddingMiddleware> logger,
        IOptions<LoadSheddingOptions> options,
        Meter meter)
    {
        _next = next;
        _logger = logger;
        _options = options.Value;

        // Create metrics using shared Meter
        _rejectedCounter = meter.CreateCounter<long>("irongate.load_shedding.rejected");
        _inFlightCounter = meter.CreateUpDownCounter<long>("irongate.requests.inflight");
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.Enabled)
        {
            await _next(context);
            return;
        }

        // Check for priority bypass
        var isPriority = context.Request.Headers["X-IronGate-Priority"] == "high" ||
                        (context.User.Identity?.IsAuthenticated == true &&
                         context.User.IsInRole("admin"));

        var maxConcurrent = isPriority
            ? (int)(_options.MaxConcurrentRequests * 1.1) // 110% for priority
            : _options.MaxConcurrentRequests;

        var currentInFlight = Interlocked.Increment(ref _currentInFlight);
        _inFlightCounter.Add(1);

        try
        {
            if (currentInFlight > maxConcurrent)
            {
                Interlocked.Decrement(ref _currentInFlight);
                _inFlightCounter.Add(-1);
                
                _rejectedCounter.Add(1);
                _logger.LogWarning("Load shedding triggered: {Current}/{Max} concurrent requests", currentInFlight, maxConcurrent);

                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentType = "application/problem+json";
                context.Response.Headers["X-IronGate-Load-Shed"] = "true";
                context.Response.Headers["Retry-After"] = "5";

                var problemDetails = new
                {
                    type = "https://httpstatuses.com/503",
                    title = "Service Unavailable",
                    status = 503,
                    detail = "Service temporarily overloaded. Please retry later.",
                    correlationId = context.Response.Headers["x-correlation-id"].ToString()
                };

                await context.Response.WriteAsJsonAsync(problemDetails, context.RequestAborted);
                return;
            }

            await _next(context);
        }
        finally
        {
            var remaining = Interlocked.Decrement(ref _currentInFlight);
            _inFlightCounter.Add(-1);
        }
    }
}