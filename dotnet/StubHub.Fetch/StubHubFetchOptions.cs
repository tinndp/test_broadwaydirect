namespace StubHub.Fetch;

/// <summary>
/// Tuning for <see cref="StubHubClient"/>. Defaults mirror
/// python/stubhub/client.py's <c>StubHubClient.__init__</c> so behaviour matches
/// the Python fetcher out of the box.
/// </summary>
public sealed class StubHubFetchOptions
{
    /// <summary>Section GETs issued per batch during the sweep.</summary>
    public int Concurrency { get; init; } = 3;

    /// <summary>Seconds between sweep batches, before adaptive backoff ramps it up.</summary>
    public double BatchDelaySeconds { get; init; } = 1.0;

    /// <summary>open + bootstrap retries (the DataDome page can tear the context
    /// down right as the first evaluate runs).</summary>
    public int Retries { get; init; } = 3;

    /// <summary>navigation attempts inside a single open.</summary>
    public int NavRetries { get; init; } = 4;

    public int TimeoutSeconds { get; init; } = 25;

    /// <summary>Seconds to let DataDome's JS challenge clear before giving up on one nav attempt.</summary>
    public double ChallengeWaitSeconds { get; init; } = 8.0;

    /// <summary>Fresh-context retry passes for sections that keep 429ing.</summary>
    public int RetryPasses { get; init; } = 2;

    public double RetryPassDelaySeconds { get; init; } = 15.0;

    /// <summary>PageSize sent on the primary <c>POST /event/&lt;id&gt;/grid</c> attempt.</summary>
    public int GridPageSize { get; init; } = 2000;

    /// <summary>Try the one-request grid POST before falling back to the section sweep.</summary>
    public bool UseGridPost { get; init; } = true;
}

/// <summary>Per-request overrides for the knobs the HTTP API exposes
/// (<c>useGridPost</c> / <c>concurrency</c> / <c>batchDelay</c>). Null = keep the
/// client default. Mirrors the request-scoped overrides in
/// python/stubhub/api.py.</summary>
public sealed record StubHubFetchOverrides(
    bool? UseGridPost = null, int? Concurrency = null, double? BatchDelaySeconds = null);
