using System.Net;
using System.Threading;
using IronGate.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Hedging;
using Polly.Timeout;
using Yarp.ReverseProxy.Forwarder;

namespace IronGate.Infrastructure.Resilience;

public sealed class ResilienceForwarderHttpClientFactory : ForwarderHttpClientFactory
{
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;
    private readonly ResilienceOptions _options;
    private readonly ILogger<ResilienceForwarderHttpClientFactory> _logger;

    public ResilienceForwarderHttpClientFactory(
        IOptions<ResilienceOptions> options,
        ILogger<ResilienceForwarderHttpClientFactory> logger)
    {
        _options = options.Value;
        _logger = logger;
        _pipeline = BuildPipeline(_options, logger);
    }

    protected override HttpMessageHandler WrapHandler(
        ForwarderHttpClientContext context,
        HttpMessageHandler handler)
    {
        if (!_options.Enabled)
        {
            return base.WrapHandler(context, handler);
        }

        return new ResilienceHttpMessageHandler(_pipeline, handler);
    }

    private static ResiliencePipeline<HttpResponseMessage> BuildPipeline(
        ResilienceOptions options,
        ILogger logger)
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();

        // 1. Outer Total Timeout (caps the entire operation including hedged attempts)
        builder.AddTimeout(new TimeoutStrategyOptions
        {
            Timeout = TimeSpan.FromSeconds(options.TotalRequestTimeoutSeconds)
        });

        // 2. Circuit Breaker (trips on 5xx or network exceptions)
        if (options.CircuitBreaker.Enabled)
        {
            builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
            {
                FailureRatio = options.CircuitBreaker.FailureRatio,
                MinimumThroughput = options.CircuitBreaker.MinimumThroughput,
                SamplingDuration = TimeSpan.FromSeconds(options.CircuitBreaker.SamplingDurationSeconds),
                BreakDuration = TimeSpan.FromSeconds(options.CircuitBreaker.BreakDurationSeconds),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .HandleResult(r => (int)r.StatusCode >= 500),
                OnOpened = args =>
                {
                    logger.LogWarning(
                        "IronGate Circuit Breaker OPENED for {BreakDuration}s due to downstream failures.",
                        args.BreakDuration.TotalSeconds);
                    return ValueTask.CompletedTask;
                },
                OnClosed = _ =>
                {
                    logger.LogInformation("IronGate Circuit Breaker CLOSED. Downstream recovered.");
                    return ValueTask.CompletedTask;
                }
            });
        }

        // 3. Speculative Request Hedging (fires parallel backup request if slow or 5xx)
        if (options.Hedging.Enabled)
        {
            builder.AddHedging(new HedgingStrategyOptions<HttpResponseMessage>
            {
                MaxHedgedAttempts = options.Hedging.MaxHedgedAttempts,
                Delay = TimeSpan.FromMilliseconds(options.Hedging.DelayMs),
                ShouldHandle = new PredicateBuilder<HttpResponseMessage>()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .HandleResult(r => (int)r.StatusCode >= 500),
                OnHedging = args =>
                {
                    logger.LogInformation(
                        "Speculative hedging triggered (Attempt #{AttemptNumber}).",
                        args.AttemptNumber);
                    return ValueTask.CompletedTask;
                }
            });
        }

        return builder.Build();
    }
}

internal sealed class ResilienceHttpMessageHandler : DelegatingHandler
{
    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;

    public ResilienceHttpMessageHandler(
        ResiliencePipeline<HttpResponseMessage> pipeline,
        HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _pipeline = pipeline;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Only hedge idempotent requests (GET, HEAD, OPTIONS, or with X-Idempotency-Key)
        if (!IsIdempotent(request))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var attempts = 0;

        try
        {
            var response = await _pipeline.ExecuteAsync(async ct =>
            {
                var currentAttempt = Interlocked.Increment(ref attempts);

                // Clone the request for hedged attempts so concurrent streams don't conflict
                using var clonedRequest = currentAttempt == 1
                    ? request
                    : CloneIdempotentRequest(request);

                return await base.SendAsync(clonedRequest, ct);
            }, cancellationToken);

            if (attempts > 1)
            {
                response.Headers.TryAddWithoutValidation("X-IronGate-Hedged", "true");
                response.Headers.TryAddWithoutValidation("X-IronGate-Attempts", attempts.ToString());
            }

            return response;
        }
        catch (BrokenCircuitException)
        {
            // Return clean 503 response with Circuit-State header so YARP forwards 503
            // (and Milestone 5 Fallback Middleware can intercept it!)
            var fallbackResponse = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(
                    """{"type":"https://httpstatuses.com/503","title":"Service Unavailable","status":503,"detail":"Downstream circuit breaker is open. Failing fast."}""",
                    System.Text.Encoding.UTF8,
                    "application/problem+json")
            };
            fallbackResponse.Headers.TryAddWithoutValidation("X-IronGate-Circuit-State", "Open");
            return fallbackResponse;
        }
    }

    private static bool IsIdempotent(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get ||
        request.Method == HttpMethod.Head ||
        request.Method == HttpMethod.Options ||
        request.Headers.Contains("X-Idempotency-Key");

    private static HttpRequestMessage CloneIdempotentRequest(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy
        };

        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }
}