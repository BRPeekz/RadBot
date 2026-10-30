namespace RadBot.Bingo;

/// <summary>Creation settings already validated by the attachment adapter.</summary>
public sealed record BingoCreation(string Name, ulong ChannelId, ulong ReportChannelId, string ImageName,
    IReadOnlyDictionary<BingoCombination, string> Prizes, TimeSpan Duration, string TimeZoneId = "America/New_York");
/// <summary>Private result of a persisted public roll.</summary>
public sealed record BingoRollResult(int Value, bool Duplicate, bool PendingClaim);

/// <summary>Transactional event orchestration. No Discord calls occur inside repository transactions.</summary>
public sealed class BingoService(IBingoRepository repository, TimeProvider clock, IBingoRandom random, BingoImageStore images)
{
    /// <summary>Returns the latest isolated snapshot.</summary>
    public Task<BingoEventState?> ReadAsync(CancellationToken cancellationToken = default) => repository.ReadAsync(cancellationToken);

    /// <summary>Recalculates the player's available prizes without reserving any global prize.</summary>
    public async Task<IReadOnlyList<BingoPrize>> GetClaimOptionsAsync(Guid eventId, ulong userId, CancellationToken cancellationToken = default)
    {
        var state = RequireActive(await repository.ReadAsync(cancellationToken), eventId);
        if (!state.Players.TryGetValue(userId, out var player)) return [];
        if (player.HasWon) throw new BingoException("You already won a prize and are inactive for this event.");
        return BingoRules.Claimable(state, player);
    }

    /// <summary>Creates one active event; completed state is archived by the repository.</summary>
    public Task<Guid> CreateAsync(BingoCreation creation, CancellationToken cancellationToken = default)
    {
        images.EnsureExists(creation.ImageName);
        if (string.IsNullOrWhiteSpace(creation.Name) || creation.Name.Length > 80 || creation.ChannelId == 0 ||
            creation.ReportChannelId == creation.ChannelId || creation.Duration < TimeSpan.FromMinutes(1) ||
            creation.Duration > TimeSpan.FromDays(30) || creation.Prizes.Count != 8 ||
            BingoRules.Lines.Keys.Any(k => !creation.Prizes.TryGetValue(k, out var value) || string.IsNullOrWhiteSpace(value) || value.Length > 200))
            throw new BingoException("Provide eight prizes (up to 200 characters), a separate staff channel, and a duration between 1 minute and 30 days.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(creation.TimeZoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new BingoException("Unknown timezone. Use America/New_York or another installed timezone identifier."); }
        return repository.TransactAsync(state =>
        {
            if (state?.Status == BingoEventStatus.Active) throw new BingoException("A Bingo event is already active.");
            var next = new BingoEventState
            {
                Name = creation.Name.Trim(), CreatedAtUtc = clock.GetUtcNow(), ChannelId = creation.ChannelId,
                ReportChannelId = creation.ReportChannelId, InstructionImagePath = creation.ImageName,
                TimeZoneId = creation.TimeZoneId, DefaultPeriodDuration = creation.Duration,
                Prizes = BingoRules.Lines.Keys.Order().Select(k => new BingoPrize { Combination = k, Description = creation.Prizes[k].Trim() }).ToList()
            };
            return (next, next.EventId);
        }, cancellationToken);
    }

    /// <summary>Starts a manual window immediately without publishing instructions, only when none is open.</summary>
    public Task<Guid> StartPeriodAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        repository.TransactAsync(state =>
        {
            var current = RequireActive(state, eventId);
            CloseExpired(current);
            if (current.Periods.Any(p => p.Status == BingoPeriodStatus.Open)) throw new BingoException("A rolling period is already open.");
            return (current, StartPeriod(current, BingoPeriodMode.Manual, null, current.DefaultPeriodDuration));
        }, cancellationToken);

    /// <summary>Validates that a live period can accept one successor before opening an announcement modal.</summary>
    public async Task<Guid> GetSchedulingPeriodIdAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        var state = RequireActive(await repository.ReadAsync(cancellationToken), eventId);
        return RequireSchedulingPeriod(state).PeriodId;
    }

    /// <summary>Saves one automatic successor and its announcement while the expected predecessor is in progress.</summary>
    public async Task ScheduleNextPeriodAsync(Guid eventId, Guid previousPeriodId, string announcement,
        CancellationToken cancellationToken = default)
    {
        var snapshot = RequireActive(await repository.ReadAsync(cancellationToken), eventId);
        ValidateAutomaticAnnouncement(snapshot, announcement);
        await repository.TransactAsync(state =>
        {
            var current = RequireActive(state, eventId);
            var previous = RequireSchedulingPeriod(current);
            if (previous.PeriodId != previousPeriodId)
                throw new BingoException("The period changed while this form was open. Use /bingo-admin open-next-period again.");
            current.NextPeriod = new BingoNextPeriodState
            {
                PreviousPeriodId = previous.PeriodId, Mode = BingoPeriodMode.Automatic, Announcement = announcement,
                Duration = current.DefaultPeriodDuration
            };
            return (current, true);
        }, cancellationToken);
    }

    /// <summary>Closes only the expected window and starts its queued successor atomically, at most once.</summary>
    public Task<bool> ClosePeriodAsync(Guid eventId, Guid periodId, CancellationToken cancellationToken = default) =>
        repository.TransactAsync(state =>
        {
            if (state?.EventId != eventId) return (state, false);
            var period = state.Periods.FirstOrDefault(p => p.PeriodId == periodId);
            if (period is null || period.Status == BingoPeriodStatus.Closed) return (state, false);
            CloseAndStartNext(state, period);
            return (state, true);
        }, cancellationToken);

    /// <summary>Closes expired windows and starts their persisted successor once, including after restart.</summary>
    public async Task RecoverAsync(CancellationToken cancellationToken = default)
    {
        var state = await repository.ReadAsync(cancellationToken);
        if (state?.Periods.Any(p => p.Status == BingoPeriodStatus.Open &&
            (p.EndUtc <= clock.GetUtcNow() || state.Status == BingoEventStatus.Ended)) != true) return;
        await repository.TransactAsync(current =>
        {
            if (current is not null) CloseExpired(current);
            return (current, true);
        }, cancellationToken);
    }

    /// <summary>Consumes exactly one attempt per player/window and durably queues only the public number.</summary>
    public Task<BingoRollResult> RollAsync(Guid eventId, ulong userId, CancellationToken cancellationToken = default) =>
        repository.TransactAsync(state =>
        {
            var current = RequireActive(state, eventId);
            var period = current.Periods.LastOrDefault();
            if (period is null || period.Status != BingoPeriodStatus.Open || period.EndUtc <= clock.GetUtcNow())
                throw new BingoException("The rolling period is closed.");
            if (!current.Players.TryGetValue(userId, out var player))
                current.Players[userId] = player = new BingoPlayerState { UserId = userId };
            if (player.HasWon) throw new BingoException("You already won a prize and cannot roll again.");
            if (BingoRules.PendingClaim(current, player)) throw new BingoException("BINGO available! Use /bingo to claim your prize!");
            if (!period.RolledUsers.Add(userId)) throw new BingoException("You already rolled during this period.");
            var value = random.Next();
            if (value is < 1 or > 9) throw new InvalidOperationException("Bingo random source returned an invalid number.");
            var duplicate = !player.UniqueNumbers.Add(value);
            player.Rolls.Add(new BingoRoll(period.PeriodId, value, clock.GetUtcNow()));
            current.Deliveries.Add(new BingoDelivery { Text = $"<@{userId}> rolled 🎲 **{value}**!" });
            return (current, new BingoRollResult(value, duplicate, BingoRules.PendingClaim(current, player)));
        }, cancellationToken);

    /// <summary>Revalidates and commits an explicit claim atomically; retrying the same flow is idempotent.</summary>
    public Task<BingoClaim> ClaimAsync(Guid eventId, ulong userId, BingoCombination combination, Guid flowId,
        CancellationToken cancellationToken = default) => repository.TransactAsync(state =>
        {
            if (state?.EventId != eventId) throw new BingoException("This component belongs to an expired event.");
            if (state.Players.TryGetValue(userId, out var existing) && existing.WinningClaim is { } awarded)
            {
                if (awarded.ClaimId == flowId && awarded.Combination == combination) return (state, awarded);
                throw new BingoException("You already won a prize.");
            }
            var current = RequireActive(state, eventId);
            if (!current.Players.TryGetValue(userId, out var player)) throw new BingoException("You have no collected numbers.");
            var prize = BingoRules.Claimable(current, player).FirstOrDefault(p => p.Combination == combination)
                ?? throw new BingoException("This prize is no longer available or the line is incomplete. Use /bingo to refresh your options.");
            var claim = new BingoClaim(flowId, combination, prize.Description, userId, clock.GetUtcNow());
            prize.Claim = claim;
            player.WinningClaim = claim;
            current.Deliveries.Add(new BingoDelivery { Text = $"BINGO! <@{userId}> won **{prize.Description}** ({BingoRules.Label(combination)})!" });
            if (current.Prizes.All(p => p.Claim is not null)) End(current);
            return (current, claim);
        }, cancellationToken);

    /// <summary>Ends an event without discarding its audit data.</summary>
    public Task<bool> EndAsync(Guid eventId, CancellationToken cancellationToken = default) => repository.TransactAsync(state =>
    {
        if (state?.EventId != eventId || state.Status == BingoEventStatus.Ended) return (state, false);
        End(state);
        return (state, true);
    }, cancellationToken);

    private void End(BingoEventState state)
    {
        state.Status = BingoEventStatus.Ended;
        state.NextPeriod = null;
        foreach (var period in state.Periods.Where(p => p.Status == BingoPeriodStatus.Open)) Close(period);
        state.Deliveries.Add(new BingoDelivery { Text = $"Bingo event **{state.Name}** has ended." });
    }

    private void Close(BingoPeriodState period)
    {
        period.Status = BingoPeriodStatus.Closed;
        period.ClosedAtUtc = clock.GetUtcNow();
    }

    private void CloseExpired(BingoEventState state)
    {
        var period = state.Periods.LastOrDefault();
        if (period?.Status == BingoPeriodStatus.Open &&
            (period.EndUtc <= clock.GetUtcNow() || state.Status == BingoEventStatus.Ended)) CloseAndStartNext(state, period);
    }

    private void CloseAndStartNext(BingoEventState state, BingoPeriodState period)
    {
        Close(period);
        if (state.NextPeriod is not { } next || next.PreviousPeriodId != period.PeriodId) return;
        state.NextPeriod = null;
        if (state.Status == BingoEventStatus.Active) StartPeriod(state, next.Mode, next.Announcement, next.Duration);
    }

    private Guid StartPeriod(BingoEventState state, BingoPeriodMode mode, string? announcement, TimeSpan duration)
    {
        var now = clock.GetUtcNow();
        var period = new BingoPeriodState
        {
            Number = state.Periods.Count + 1, StartUtc = now, EndUtc = now + duration, Mode = mode
        };
        state.Periods.Add(period);
        if (mode == BingoPeriodMode.Automatic)
            state.Deliveries.Add(new BingoDelivery { Text = announcement!, IncludeImage = true, PeriodId = period.PeriodId });
        return period.PeriodId;
    }

    private BingoPeriodState RequireSchedulingPeriod(BingoEventState state)
    {
        var period = state.Periods.LastOrDefault();
        if (period is null || period.Status != BingoPeriodStatus.Open || period.EndUtc <= clock.GetUtcNow())
            throw new BingoException("A rolling period must be in progress to schedule its successor. Use /bingo-admin start-period when no period is open.");
        if (state.NextPeriod is not null) throw new BingoException("The next period is already scheduled.");
        return period;
    }

    private void ValidateAutomaticAnnouncement(BingoEventState state, string announcement)
    {
        images.EnsureExists(state.InstructionImagePath);
        if (string.IsNullOrWhiteSpace(announcement) || announcement.Length > 4000)
            throw new BingoException("The next period requires announcement text of 1–4000 characters.");
    }

    private static BingoEventState RequireActive(BingoEventState? state, Guid? eventId = null)
    {
        if (state is null || (eventId.HasValue && state.EventId != eventId.Value) || state.Status != BingoEventStatus.Active)
            throw new BingoException("No active Bingo event, or this interaction belongs to an expired event.");
        return state;
    }
}
