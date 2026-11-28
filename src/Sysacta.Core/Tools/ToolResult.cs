using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Sysacta.Core.Tools;

/// <summary>Spec §9 status values.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ToolStatus>))]
public enum ToolStatus
{
    [JsonStringEnumMemberName("success")] Success,
    [JsonStringEnumMemberName("error")] Error,
    [JsonStringEnumMemberName("partial")] Partial,
    /// <summary>It is unknown whether the side effect happened (e.g. timeout after the OS call started).</summary>
    [JsonStringEnumMemberName("unknown")] Unknown,
}

/// <summary>Spec §9 verification values.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<Verification>))]
public enum Verification
{
    /// <summary>The resulting state was read back and matches.</summary>
    [JsonStringEnumMemberName("verified")] Verified,
    /// <summary>The OS accepted the request; completion is not confirmed.</summary>
    [JsonStringEnumMemberName("accepted")] Accepted,
    /// <summary>Nothing to verify (read-only tools).</summary>
    [JsonStringEnumMemberName("not_applicable")] NotApplicable,
    [JsonStringEnumMemberName("unknown")] Unknown,
}

/// <summary>Common error codes (spec §9). Never invent ad-hoc codes in tools; add them here.</summary>
public static class ErrorCodes
{
    public const string InvalidArgument = "INVALID_ARGUMENT";
    public const string ToolDisabled = "TOOL_DISABLED";
    public const string PermissionDenied = "PERMISSION_DENIED";
    public const string AdminRequired = "ADMIN_REQUIRED";
    public const string NotSupported = "NOT_SUPPORTED";
    public const string TargetNotFound = "TARGET_NOT_FOUND";
    public const string AmbiguousTarget = "AMBIGUOUS_TARGET";
    public const string TargetChanged = "TARGET_CHANGED";
    public const string DeviceDisconnected = "DEVICE_DISCONNECTED";
    public const string NoActiveSession = "NO_ACTIVE_SESSION";
    public const string NotificationsDisabled = "NOTIFICATIONS_DISABLED";
    public const string Busy = "BUSY";
    public const string Timeout = "TIMEOUT";
    public const string Cancelled = "CANCELLED";
    public const string VerificationFailed = "VERIFICATION_FAILED";
    public const string OsApiError = "OS_API_ERROR";
    public const string AuditUnavailable = "AUDIT_UNAVAILABLE";
    public const string InternalError = "INTERNAL_ERROR";
}

public sealed record ToolError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable);

/// <summary>
/// The structured result envelope returned for every tool call (spec §9).
/// Build it through the factory methods; the executor fills <see cref="RequestId"/>.
/// </summary>
public sealed record ToolResult
{
    [JsonPropertyName("status")] public required ToolStatus Status { get; init; }

    [JsonPropertyName("verification"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Verification? Verification { get; init; }

    [JsonPropertyName("data"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Data { get; init; }

    [JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ToolError? Error { get; init; }

    [JsonPropertyName("request_id")] public string RequestId { get; init; } = "";

    public bool IsSuccess => Status == ToolStatus.Success;

    public static ToolResult Fail(string code, string message, bool retryable = false) =>
        new() { Status = ToolStatus.Error, Error = new ToolError(code, message, retryable) };

    /// <summary>The side effect may or may not have happened. Never retryable.</summary>
    public static ToolResult Unknown(string code, string message) =>
        new() { Status = ToolStatus.Unknown, Verification = Tools.Verification.Unknown, Error = new ToolError(code, message, false) };
}

/// <summary>What a tool returns on completion. The executor wraps it into a <see cref="ToolResult"/>.</summary>
public sealed record ToolOutcome<TData>(TData Data, Verification Verification)
{
    public ToolStatus Status { get; internal init; } = ToolStatus.Success;
    public ToolError? Error { get; internal init; }
}

/// <summary>Factories for non-success <see cref="ToolOutcome{TData}"/> values.</summary>
public static class ToolOutcome
{
    public static ToolOutcome<TData> Partial<TData>(TData data, Verification verification, string code, string message) =>
        new(data, verification) { Status = ToolStatus.Partial, Error = new ToolError(code, message, false) };

    public static ToolOutcome<TData> Unknown<TData>(TData data, string code, string message) =>
        new(data, Verification.Unknown) { Status = ToolStatus.Unknown, Error = new ToolError(code, message, false) };

    /// <summary>
    /// The request failed and the resulting state is known; <paramref name="data"/> explains it (for example per-device
    /// results of a batch). Use <see cref="Partial"/> when some of the requested changes were applied, and
    /// <see cref="ToolFailureException"/> when there is nothing to report.
    /// </summary>
    public static ToolOutcome<TData> Error<TData>(TData data, string code, string message, bool retryable = false) =>
        new(data, Verification.Unknown) { Status = ToolStatus.Error, Error = new ToolError(code, message, retryable) };
}

/// <summary>
/// Throw from a state-changing tool when a failure happens after the change may already have been applied (for
/// example the read-back for verification fails). The executor returns <see cref="ToolStatus.Unknown"/> so clients do
/// not treat the change as not applied. The message must not contain secrets, paths or raw exception text.
/// </summary>
public sealed class ToolOutcomeUnknownException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Throw from a tool to report an expected failure with a common error code.
/// The message is returned to the client, so it must not contain secrets, paths or raw exception text.
/// </summary>
public sealed class ToolFailureException(string code, string message, bool retryable = false) : Exception(message)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}
