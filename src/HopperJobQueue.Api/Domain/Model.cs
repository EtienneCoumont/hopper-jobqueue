namespace HopperJobQueue.Api.Domain;

public static class JobStatus
{
    public const string Pending = "pending";
    public const string Leased = "leased";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";

    public static readonly string[] All = [Pending, Leased, Done, Failed, Expired, Cancelled];
    public static readonly string[] Terminal = [Done, Failed, Expired, Cancelled];
}

public static class ApiScope
{
    public const string Producer = "producer";
    public const string Worker = "worker";
    public const string Admin = "admin";

    public static readonly string[] All = [Producer, Worker, Admin];
}

public sealed class Job
{
    public long Id { get; set; }
    public string IdempotencyKey { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Project { get; set; }
    public string Payload { get; set; } = "null";
    public string Status { get; set; } = JobStatus.Pending;
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public string? WorkerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Result { get; set; }
    public string? LastError { get; set; }
}

public sealed class JobKind
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public int DefaultTtlSeconds { get; set; } = 86400;
    public int DefaultMaxAttempts { get; set; } = 3;
    public int DefaultLeaseSeconds { get; set; } = 1200;
    public int RetentionDays { get; set; } = 90;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ApiKeyRecord
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Prefix { get; set; } = "";
    public byte[] KeyHash { get; set; } = [];
    public string Scope { get; set; } = "";
    public string[] AllowedKinds { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class JobEvent
{
    public long Id { get; set; }
    public long JobId { get; set; }
    public DateTimeOffset At { get; set; }
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = "";
    public string Actor { get; set; } = "";
    public string? Note { get; set; }
}
