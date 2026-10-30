using System.Text.Json.Serialization;

namespace RadBot.Bingo;

/// <summary>Stable identifiers for the eight fixed lines; numeric values are persisted.</summary>
public enum BingoCombination { RowTop, RowMiddle, RowBottom, ColumnLeft, ColumnMiddle, ColumnRight, DiagonalDown, DiagonalUp }
/// <summary>Lifecycle of a durable Bingo event.</summary>
public enum BingoEventStatus { Active, Ended }
/// <summary>Lifecycle of a rolling window.</summary>
public enum BingoPeriodStatus { Open, Closed }
/// <summary>Controls announcement publication, not whether a period expires.</summary>
public enum BingoPeriodMode { Manual, Automatic }

/// <summary>A durable multi-period event, independent of legacy rounds.</summary>
public sealed class BingoEventState
{
    /// <summary>Schema version, validated before any write.</summary>
    [JsonRequired]
    public int Version { get; set; } = 1;
    /// <summary>Unique event identifier.</summary>
    [JsonRequired]
    public Guid EventId { get; set; } = Guid.NewGuid();
    /// <summary>Display name.</summary>
    public string Name { get; set; } = "Bingo";
    /// <summary>Current lifecycle.</summary>
    [JsonRequired]
    public BingoEventStatus Status { get; set; }
    /// <summary>Creation instant.</summary>
    [JsonRequired]
    public DateTimeOffset CreatedAtUtc { get; set; }
    /// <summary>Presentation timezone.</summary>
    public string TimeZoneId { get; set; } = "America/New_York";
    /// <summary>Generated image filename relative to the data image directory.</summary>
    public string InstructionImagePath { get; set; } = "";
    /// <summary>Duration used for new windows.</summary>
    public TimeSpan DefaultPeriodDuration { get; set; } = TimeSpan.FromHours(24);
    /// <summary>Public game channel.</summary>
    [JsonRequired]
    public ulong ChannelId { get; set; }
    /// <summary>Private staff channel; zero disables reporting.</summary>
    public ulong ReportChannelId { get; set; }
    /// <summary>All windows, including closed windows for audit.</summary>
    [JsonRequired]
    public List<BingoPeriodState> Periods { get; set; } = [];
    /// <summary>One configured successor, consumed atomically when its predecessor closes; absent in older JSON.</summary>
    public BingoNextPeriodState? NextPeriod { get; set; }
    /// <summary>Eight global prizes.</summary>
    [JsonRequired]
    public List<BingoPrize> Prizes { get; set; } = [];
    /// <summary>Participants indexed by Discord user identifier.</summary>
    [JsonRequired]
    public Dictionary<ulong, BingoPlayerState> Players { get; set; } = [];
    /// <summary>Durable staff report pages.</summary>
    public List<ulong> AdministrativeReportMessageIds { get; set; } = [];
    /// <summary>Pending public deliveries, retried after reconnect.</summary>
    [JsonRequired]
    public List<BingoDelivery> Deliveries { get; set; } = [];
}

/// <summary>Administrator inputs retained until the current window closes.</summary>
public sealed class BingoNextPeriodState
{
    /// <summary>The open window whose closure triggers this successor.</summary>
    [JsonRequired]
    public Guid PreviousPeriodId { get; set; }
    /// <summary>Whether to publish the saved announcement and event image at opening.</summary>
    [JsonRequired]
    public BingoPeriodMode Mode { get; set; }
    /// <summary>Saved long-form announcement; null for a manual announcement.</summary>
    [JsonRequired]
    public string? Announcement { get; set; }
    /// <summary>Captured duration so a future setting change cannot alter this request.</summary>
    [JsonRequired]
    public TimeSpan Duration { get; set; }
}

/// <summary>A single persisted rolling window.</summary>
public sealed class BingoPeriodState
{
    /// <summary>Window identifier.</summary>
    [JsonRequired]
    public Guid PeriodId { get; set; } = Guid.NewGuid();
    /// <summary>One-based sequence.</summary>
    public int Number { get; set; }
    /// <summary>Opening instant.</summary>
    [JsonRequired]
    public DateTimeOffset StartUtc { get; set; }
    /// <summary>Scheduled deadline.</summary>
    [JsonRequired]
    public DateTimeOffset EndUtc { get; set; }
    /// <summary>Actual closing instant.</summary>
    public DateTimeOffset? ClosedAtUtc { get; set; }
    /// <summary>Window lifecycle.</summary>
    [JsonRequired]
    public BingoPeriodStatus Status { get; set; }
    /// <summary>Announcement mode.</summary>
    [JsonRequired]
    public BingoPeriodMode Mode { get; set; }
    /// <summary>Users whose attempt has been consumed.</summary>
    [JsonRequired]
    public HashSet<ulong> RolledUsers { get; set; } = [];
    /// <summary>Automatically published messages.</summary>
    public List<ulong> AnnouncementMessageIds { get; set; } = [];
}

/// <summary>Accumulated numbers and immutable audit entries for one player.</summary>
public sealed class BingoPlayerState
{
    /// <summary>Discord user identifier.</summary>
    public ulong UserId { get; set; }
    /// <summary>Unique accumulated values.</summary>
    [JsonRequired]
    public HashSet<int> UniqueNumbers { get; set; } = [];
    /// <summary>All attempts, including duplicates.</summary>
    [JsonRequired]
    public List<BingoRoll> Rolls { get; set; } = [];
    /// <summary>Winning claim, or null until this player wins.</summary>
    [JsonRequired]
    public BingoClaim? WinningClaim { get; set; }
    /// <summary>Winners can never roll again.</summary>
    public bool HasWon => WinningClaim is not null;
}

/// <summary>An audited attempt.</summary>
public sealed record BingoRoll(Guid PeriodId, int Value, DateTimeOffset TimestampUtc);
/// <summary>Snapshot of a confirmed award.</summary>
public sealed record BingoClaim(Guid ClaimId, BingoCombination Combination, string PrizeDescription, ulong UserId, DateTimeOffset ClaimedAtUtc);
/// <summary>A globally unique prize.</summary>
public sealed class BingoPrize
{
    /// <summary>Fixed line identifier.</summary>
    [JsonRequired]
    public BingoCombination Combination { get; set; }
    /// <summary>Configured award description.</summary>
    public string Description { get; set; } = "";
    /// <summary>Confirmed award, or null while available.</summary>
    [JsonRequired]
    public BingoClaim? Claim { get; set; }
}

/// <summary>Persisted public presentation work, independent of state transactions.</summary>
public sealed class BingoDelivery
{
    /// <summary>Delivery identifier.</summary>
    [JsonRequired]
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Public text only; never personal collections or staff data.</summary>
    public string Text { get; set; } = "";
    /// <summary>Attach the durable instruction image.</summary>
    public bool IncludeImage { get; set; }
    /// <summary>Related period, when announcing a window.</summary>
    public Guid? PeriodId { get; set; }
    /// <summary>One ID per successfully published text page.</summary>
    public List<ulong> MessageIds { get; set; } = [];
    /// <summary>Whether every page has been delivered.</summary>
    public bool Completed { get; set; }
}
