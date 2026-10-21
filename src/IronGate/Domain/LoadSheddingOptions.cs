namespace IronGate.Domain;

public sealed class LoadSheddingOptions
{
    public const string SectionName = "LoadShedding";

    public bool Enabled { get; set; } = true;
    public int MaxConcurrentRequests { get; set; } = 500;
    public int QueueLimit { get; set; } = 0;
}