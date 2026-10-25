namespace RoomOps.Domain.Common;

/// <summary>Classifies failures so the UI can explain them and the executor can decide whether to retry.</summary>
public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
    PermissionDenied,
    Authentication,
    Throttled,
    Transient,
    Unsupported,
    TenantMismatch,
    Stale,
    NotConnected,
    Internal,
}

public sealed record DomainError(string Code, string Message, ErrorKind Kind, string? Detail = null);

public class RoomOpsException : Exception
{
    public RoomOpsException(DomainError error, Exception? inner = null)
        : base(error.Message, inner)
    {
        Error = error;
    }

    public DomainError Error { get; }

    public static RoomOpsException Validation(string code, string message) =>
        new(new DomainError(code, message, ErrorKind.Validation));

    public static RoomOpsException NotFound(string code, string message) =>
        new(new DomainError(code, message, ErrorKind.NotFound));

    public static RoomOpsException NotConnected(string message) =>
        new(new DomainError("session.notConnected", message, ErrorKind.NotConnected));

    public static RoomOpsException TenantMismatch(string message) =>
        new(new DomainError("session.tenantMismatch", message, ErrorKind.TenantMismatch));

    public static RoomOpsException Unsupported(string code, string message) =>
        new(new DomainError(code, message, ErrorKind.Unsupported));
}

/// <summary>The system that is authoritative for a property. Shown next to values in the UI.</summary>
public enum SourceSystem
{
    ExchangeOnline,
    MicrosoftPlaces,
    MicrosoftGraph,
    RoomOpsLocal,
}

public enum IssueLevel
{
    Error,
    Warning,
    Info,
}

/// <summary>A field-level validation result. Errors block execution; warnings are shown in the preview.</summary>
public sealed record ValidationIssue(string Field, string Message, IssueLevel Level);
