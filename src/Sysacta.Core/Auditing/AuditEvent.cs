namespace Sysacta.Core.Auditing;

public enum AuditOutcome
{
    /// <summary>Intent recorded before a state-changing operation; replaced by the final outcome.</summary>
    Pending,
    Success,
    Error,
    Partial,
    /// <summary>The side effect may or may not have happened.</summary>
    Unknown,
    /// <summary>Rejected before execution (permission, availability, invalid arguments, busy).</summary>
    Denied,
    AuthFailed,
}

/// <summary>
/// One audit record. Never put token text, Authorization headers, request/response bodies or raw exception text here.
/// <see cref="Arguments"/> contains only the tool's allow-listed arguments (see ToolDescriptor.AuditedArguments).
/// </summary>
public sealed record AuditEvent
{
    public required string RequestId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public string? ClientId { get; init; }
    public string? ClientName { get; init; }
    public string? ToolName { get; init; }
    /// <summary>"mcp", "local_console" or "http" (authentication failures).</summary>
    public required string Source { get; init; }
    public string? Arguments { get; init; }
    public required AuditOutcome Outcome { get; init; }
    public string? ErrorCode { get; init; }
    public long? DurationMs { get; init; }
    /// <summary>Socket peer address only; forwarded headers are never trusted here.</summary>
    public string? RemoteAddress { get; init; }

    /// <summary>
    /// Set on an aggregated record: this many rejections with the same <see cref="ErrorCode"/> happened in the window
    /// starting at <see cref="Timestamp"/> and were not recorded individually. Null for an individual record.
    /// </summary>
    public long? AggregateCount { get; init; }
}

/// <summary>Audit sink. <see cref="Write"/> inserts, or replaces the record with the same <see cref="AuditEvent.RequestId"/>.</summary>
public interface IAuditLog
{
    /// <summary>
    /// Must throw on failure, including when the record does not fit the storage limits; the executor refuses state
    /// changes when the intent (<see cref="AuditOutcome.Pending"/>) cannot be persisted.
    /// </summary>
    void Write(AuditEvent auditEvent);
}
