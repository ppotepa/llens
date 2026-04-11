using System.Collections.Concurrent;

namespace Llens.Observability;

public class QueryTelemetry
{
    private const int MaxRecent = 400;
    private readonly ConcurrentQueue<QueryTelemetryEvent> _recent = new();
    private long _total;
    private long _empty;
    private long _fallback;

    public void Record(
        string endpoint,
        string mode,
        string project,
        long elapsedMs,
        int resultCount,
        bool isEmpty,
        bool usedFallback,
        int estimatedTokens)
    {
        Interlocked.Increment(ref _total);
        if (isEmpty) Interlocked.Increment(ref _empty);
        if (usedFallback) Interlocked.Increment(ref _fallback);

        _recent.Enqueue(new QueryTelemetryEvent
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Endpoint = endpoint,
            Mode = mode,
            Project = project,
            LatencyMs = elapsedMs,
            ResultCount = resultCount,
            IsEmpty = isEmpty,
            UsedFallback = usedFallback,
            EstimatedTokens = estimatedTokens
        });

        while (_recent.Count > MaxRecent && _recent.TryDequeue(out _)) { }
    }

    public QueryTelemetrySnapshot Snapshot()
    {
        var recent = _recent.ToArray();
        var fallbackRate = _total == 0 ? 0 : (double)_fallback / _total;
        var emptyRate = _total == 0 ? 0 : (double)_empty / _total;
        return new QueryTelemetrySnapshot
        {
            TotalQueries = _total,
            EmptyResults = _empty,
            FallbackCount = _fallback,
            EmptyRate = emptyRate,
            FallbackRate = fallbackRate,
            Recent = [.. recent
                .OrderByDescending(r => r.TimestampUtc)
                .Take(100)]
        };
    }
}

public class QueryTelemetrySnapshot
{
    public long TotalQueries { get; set; }
    public long EmptyResults { get; set; }
    public long FallbackCount { get; set; }
    public double EmptyRate { get; set; }
    public double FallbackRate { get; set; }
    public List<QueryTelemetryEvent> Recent { get; set; } = [];
}

public class QueryTelemetryEvent
{
    public DateTimeOffset TimestampUtc { get; set; }
    public string Endpoint { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Project { get; set; } = "";
    public long LatencyMs { get; set; }
    public int ResultCount { get; set; }
    public bool IsEmpty { get; set; }
    public bool UsedFallback { get; set; }
    public int EstimatedTokens { get; set; }
}
