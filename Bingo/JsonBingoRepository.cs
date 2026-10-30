using System.Text.Json;
using System.Text.Json.Serialization;

namespace RadBot.Bingo;

/// <summary>Configured durable data location, independent of the working directory.</summary>
public sealed class BingoOptions
{
    /// <summary>Absolute directory; override with RADBOT_DATA_DIRECTORY.</summary>
    public string DataDirectory { get; init; } = Path.GetFullPath(
        Environment.GetEnvironmentVariable("RADBOT_DATA_DIRECTORY") ?? Path.Combine(AppContext.BaseDirectory, "data"));
}

/// <summary>Serialized transactions; callbacks must be synchronous and must not perform network I/O.</summary>
public interface IBingoRepository
{
    /// <summary>Loads an isolated snapshot, or null when no event exists.</summary>
    Task<BingoEventState?> ReadAsync(CancellationToken cancellationToken = default);
    /// <summary>Reloads, validates, mutates and durably commits under one exclusive lock.</summary>
    Task<T> TransactAsync<T>(Func<BingoEventState?, (BingoEventState? State, T Result)> change, CancellationToken cancellationToken = default);
}

/// <summary>Single-process JSON repository with same-volume atomic replacement and non-destructive archives.</summary>
public sealed class JsonBingoRepository(BingoOptions options) : IBingoRepository, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory = options.DataDirectory;
    private string StatePath => Path.Combine(_directory, "bingo-event.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true, Converters = { new JsonStringEnumConverter() }
    };

    /// <inheritdoc />
    public async Task<BingoEventState?> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return await LoadAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    /// <inheritdoc />
    public async Task<T> TransactAsync<T>(Func<BingoEventState?, (BingoEventState? State, T Result)> change, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var previous = await LoadAsync(cancellationToken);
            var previousId = previous?.EventId;
            var (state, result) = change(previous);
            if (state is null)
            {
                if (previous is not null) throw new InvalidOperationException("Removing persisted Bingo data is prohibited.");
                return result;
            }
            Validate(state);
            Directory.CreateDirectory(_directory);
            if (previousId is not null && previousId != state.EventId)
            {
                var archive = Path.Combine(_directory, $"bingo-{previousId:N}.json");
                if (!File.Exists(archive)) File.Copy(StatePath, archive, overwrite: false);
            }
            var temporary = Path.Combine(_directory, $".bingo-{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(StatePath)) File.Replace(temporary, StatePath, StatePath + ".bak");
                else File.Move(temporary, StatePath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return result;
        }
        finally { _gate.Release(); }
    }

    private async Task<BingoEventState?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StatePath)) return null;
        try
        {
            await using var stream = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            var state = await JsonSerializer.DeserializeAsync<BingoEventState>(stream, JsonOptions, cancellationToken)
                ?? throw new JsonException("Null Bingo document.");
            Validate(state);
            return state;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NotSupportedException)
        {
            Console.Error.WriteLine("Bingo storage is invalid or unsupported. Original file preserved; restore a verified backup before retrying.");
            throw new InvalidDataException("Bingo storage is unavailable; ask an administrator to inspect the data file.", ex);
        }
    }

    private static void Validate(BingoEventState state)
    {
        if (state.Version != 1 || state.EventId == Guid.Empty || !Enum.IsDefined(state.Status) ||
            state.ChannelId == 0 || state.ReportChannelId == state.ChannelId || string.IsNullOrWhiteSpace(state.Name) ||
            state.CreatedAtUtc == default || state.CreatedAtUtc.Offset != TimeSpan.Zero ||
            state.DefaultPeriodDuration < TimeSpan.FromMinutes(1) || state.DefaultPeriodDuration > TimeSpan.FromDays(30) ||
            state.Prizes is null || state.Prizes.Count != 8 || state.Prizes.Any(p => p is null) ||
            state.Prizes.Select(p => p.Combination).Distinct().Count() != 8 ||
            state.Prizes.Any(p => p is null || !BingoRules.Lines.ContainsKey(p.Combination) || string.IsNullOrWhiteSpace(p.Description)) ||
            state.Players is null || state.Periods is null || state.Deliveries is null || state.AdministrativeReportMessageIds is null ||
            state.Periods.Any(p => p is null || p.PeriodId == Guid.Empty || !Enum.IsDefined(p.Status) || !Enum.IsDefined(p.Mode) ||
                p.StartUtc == default || p.EndUtc <= p.StartUtc || p.StartUtc.Offset != TimeSpan.Zero || p.EndUtc.Offset != TimeSpan.Zero ||
                p.RolledUsers is null || p.AnnouncementMessageIds is null || (p.Status == BingoPeriodStatus.Closed && p.ClosedAtUtc is null)) ||
            state.Periods.Select(p => p.PeriodId).Distinct().Count() != state.Periods.Count ||
            !state.Periods.Select(p => p.Number).SequenceEqual(Enumerable.Range(1, state.Periods.Count)) ||
            state.Periods.Count(p => p.Status == BingoPeriodStatus.Open) > 1 ||
            state.Players.Any(p => p.Value is null || p.Key != p.Value.UserId || p.Value.UniqueNumbers is null ||
                p.Value.Rolls is null || p.Value.UniqueNumbers.Any(n => n is < 1 or > 9) ||
                p.Value.Rolls.Any(r => r is null || r.Value is < 1 or > 9 || r.TimestampUtc.Offset != TimeSpan.Zero ||
                    !state.Periods.Any(w => w.PeriodId == r.PeriodId && w.RolledUsers.Contains(p.Key))) ||
                p.Value.Rolls.GroupBy(r => r.PeriodId).Any(g => g.Count() > 1)) ||
            state.Deliveries.Any(d => d is null || d.Id == Guid.Empty || string.IsNullOrWhiteSpace(d.Text) || d.MessageIds is null ||
                (d.PeriodId is { } id && !state.Periods.Any(p => p.PeriodId == id))) ||
            state.Deliveries.Select(d => d.Id).Distinct().Count() != state.Deliveries.Count ||
            state.Prizes.Where(p => p.Claim is not null).GroupBy(p => p.Claim!.UserId).Any(g => g.Count() > 1))
            throw new InvalidDataException("Unsupported or inconsistent Bingo state.");

        if (state.NextPeriod is { } next &&
            (state.Status != BingoEventStatus.Active || !Enum.IsDefined(next.Mode) ||
             next.Duration < TimeSpan.FromMinutes(1) || next.Duration > TimeSpan.FromDays(30) ||
             state.Periods.LastOrDefault() is not { Status: BingoPeriodStatus.Open } previous ||
             previous.PeriodId != next.PreviousPeriodId ||
             (next.Mode == BingoPeriodMode.Automatic && (string.IsNullOrWhiteSpace(next.Announcement) || next.Announcement.Length > 4000)) ||
             (next.Mode == BingoPeriodMode.Manual && next.Announcement is not null)))
            throw new InvalidDataException("Invalid scheduled Bingo successor.");

        foreach (var prize in state.Prizes.Where(p => p.Claim is not null))
        {
            var claim = prize.Claim!;
            if (claim.ClaimId == Guid.Empty || claim.Combination != prize.Combination || claim.ClaimedAtUtc == default ||
                claim.ClaimedAtUtc.Offset != TimeSpan.Zero || string.IsNullOrWhiteSpace(claim.PrizeDescription) ||
                !state.Players.TryGetValue(claim.UserId, out var winner) || winner.WinningClaim != claim ||
                !BingoRules.Lines[prize.Combination].All(winner.UniqueNumbers.Contains))
                throw new InvalidDataException("Inconsistent Bingo prize/winner claim.");
        }
        if (state.Players.Values.Any(p => p.WinningClaim is { } claim &&
            !state.Prizes.Any(prize => prize.Claim == claim && claim.UserId == p.UserId)))
            throw new InvalidDataException("Bingo winner is missing the matching prize claim.");
    }

    /// <summary>Releases the transaction semaphore.</summary>
    public void Dispose() => _gate.Dispose();
}
