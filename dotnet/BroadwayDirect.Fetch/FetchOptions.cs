namespace BroadwayDirect.Fetch;

public class FetchOptions
{
    public double Sleep { get; init; } = 0.3;
    public int Retries { get; init; } = 3;
    public int TimeoutSeconds { get; init; } = 20;
    public int Concurrency { get; init; } = 6;
}
