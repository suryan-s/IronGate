namespace IronGate.Domain;

public sealed class ResilienceOptions
{
    public const string SectionName = "Resilience";

    public bool Enabled { get; set; } = true;
    public int TotalRequestTimeoutSeconds { get; set; } = 10;
    public HedgingConfig Hedging { get; set; } = new();
    public CircuitBreakerConfig CircuitBreaker { get; set; } = new();
}

public sealed class HedgingConfig
{
    public bool Enabled { get; set; } = true;
    public int MaxHedgedAttempts { get; set; } = 2;
    public int DelayMs { get; set; } = 150;
}

public sealed class CircuitBreakerConfig
{
    public bool Enabled { get; set; } = true;
    public double FailureRatio { get; set; } = 0.5;
    public int MinimumThroughput { get; set; } = 5;
    public int SamplingDurationSeconds { get; set; } = 10;
    public int BreakDurationSeconds { get; set; } = 15;
}