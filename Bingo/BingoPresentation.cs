using Discord;
using Discord.Net;
using RadBot.Services;
using System.Net;

namespace RadBot.Bingo;

/// <summary>Testable Discord publication boundary.</summary>
public interface IBingoPublisher
{
    /// <summary>Sends public or staff text with mentions disabled and an optional local attachment.</summary>
    Task<ulong> SendAsync(ulong channelId, string text, string? imagePath, CancellationToken cancellationToken);
    /// <summary>Updates a report, returning false if the message was deleted.</summary>
    Task<bool> UpdateAsync(ulong channelId, ulong messageId, string text, CancellationToken cancellationToken);
}

/// <summary>Discord.Net transport; no game rules.</summary>
public sealed class DiscordBingoPublisher(BotState state) : IBingoPublisher
{
    private IMessageChannel Channel(ulong id) => state.Client.GetChannel(id) as IMessageChannel
        ?? throw new BingoException("The configured channel is missing or inaccessible. Ask an administrator to restore it.");

    /// <inheritdoc />
    public async Task<ulong> SendAsync(ulong channelId, string text, string? imagePath, CancellationToken cancellationToken)
    {
        var channel = Channel(channelId);
        var options = new RequestOptions { CancelToken = cancellationToken };
        if (imagePath is null)
            return (await channel.SendMessageAsync(text, allowedMentions: AllowedMentions.None, options: options)).Id;
        await using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        return (await channel.SendFileAsync(stream, Path.GetFileName(imagePath), text, options: options, allowedMentions: AllowedMentions.None)).Id;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateAsync(ulong channelId, ulong messageId, string text, CancellationToken cancellationToken)
    {
        var options = new RequestOptions { CancelToken = cancellationToken };
        try
        {
            if (await Channel(channelId).GetMessageAsync(messageId, options: options) is not IUserMessage message) return false;
            await message.ModifyAsync(p => { p.Content = text; p.AllowedMentions = AllowedMentions.None; }, options);
            return true;
        }
        catch (HttpException ex) when (ex.HttpCode == HttpStatusCode.NotFound && ex.DiscordCode == DiscordErrorCode.UnknownMessage)
        { return false; }
    }
}

/// <summary>Discord text formatting with bounded pages and no personal data in public status.</summary>
public static class BingoText
{
    /// <summary>Splits at line boundaries where possible without discarding content or splitting UTF-16 surrogate pairs.</summary>
    public static IReadOnlyList<string> Pages(string text, int limit = 1900)
    {
        if (limit < 2) throw new ArgumentOutOfRangeException(nameof(limit));
        var pages = new List<string>();
        while (text.Length > limit)
        {
            var cut = text.LastIndexOf('\n', limit - 1, limit);
            cut = cut < limit / 2 ? limit : cut + 1;
            if (char.IsHighSurrogate(text[cut - 1])) cut--;
            pages.Add(text[..cut]);
            text = text[cut..];
        }
        if (text.Length > 0) pages.Add(text);
        return pages;
    }

    /// <summary>Private player status, without disclosing other players' collections.</summary>
    public static string Status(BingoEventState state, ulong userId)
    {
        var period = state.Periods.LastOrDefault();
        var periodText = period is null ? "No rolling period" :
            $"Period #{period.Number}: {period.Status}; deadline <t:{period.EndUtc.ToUnixTimeSeconds()}:F> (<t:{period.EndUtc.ToUnixTimeSeconds()}:R>)";
        var own = state.Players.GetValueOrDefault(userId);
        var situation = own?.HasWon == true ? "Winner (inactive)" : own is not null && BingoRules.PendingClaim(state, own)
            ? "Pending claim — use /bingo" : "No prize won";
        return $"**{state.Name}** — {state.Status}\n{periodText}\n" +
            string.Join("\n", state.Prizes.Select(p => $"{BingoRules.Label(p.Combination)} — {p.Description}: {(p.Claim is null ? "Available" : "Claimed")}")) +
            $"\nYour situation: {situation}";
    }

    /// <summary>Staff-only audit pages, never intended for the public game channel.</summary>
    public static IReadOnlyList<string> Report(BingoEventState state)
    {
        var period = state.Periods.LastOrDefault();
        var text = $"BINGO STAFF REPORT — {state.Name} — {state.Status}\nEvent: {state.EventId}\n";
        if (period is not null)
            text += $"Period #{period.Number}: {period.Status} ({period.Mode})\nOpen <t:{period.StartUtc.ToUnixTimeSeconds()}:F> / Deadline <t:{period.EndUtc.ToUnixTimeSeconds()}:F>\n" +
                (period.ClosedAtUtc is { } closed ? $"Closed <t:{closed.ToUnixTimeSeconds()}:F>\n" : "");
        if (state.NextPeriod is { } next)
            text += $"Next period #{state.Periods.Count + 1} scheduled: starts when the current period closes; duration {next.Duration}.\n";
        foreach (var prize in state.Prizes)
            text += $"{BingoRules.Label(prize.Combination)} — {prize.Description}: " +
                (prize.Claim is { } claim ? $"CLAIMED by <@{claim.UserId}> <t:{claim.ClaimedAtUtc.ToUnixTimeSeconds()}:F>" : "AVAILABLE") + "\n";
        text += $"Participants: {state.Players.Count}\n";
        foreach (var player in state.Players.Values.OrderBy(p => p.UserId))
            text += $"<@{player.UserId}>: [{string.Join(", ", player.UniqueNumbers.Order())}] — " +
                $"{(player.HasWon ? "WINNER" : BingoRules.PendingClaim(state, player) ? "PENDING CLAIM" : "eligible")}; " +
                $"rolled this period: {period?.RolledUsers.Contains(player.UserId) == true}; attempts: {player.Rolls.Count}\n";
        return Pages(text);
    }

}

/// <summary>Idempotent report updates and persisted outbox draining, serialized separately from state transactions.</summary>
public sealed class BingoAnnouncementService(IBingoRepository repository, IBingoPublisher publisher, BingoImageStore images, TimeProvider clock)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _reportFingerprint;
    private DateTimeOffset _lastReportCheck;

    /// <summary>Publishes pending work and refreshes staff pages; no repository lock spans network I/O.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var snapshot = await repository.ReadAsync(cancellationToken);
            if (snapshot is null) return;
            foreach (var delivery in snapshot.Deliveries.Where(d => !d.Completed))
            {
                try
                {
                    var pages = BingoText.Pages(delivery.Text);
                    for (int i = delivery.MessageIds.Count; i < pages.Count; i++)
                    {
                        string? image = null;
                        if (i == 0 && delivery.IncludeImage)
                        {
                            images.EnsureExists(snapshot.InstructionImagePath);
                            image = images.GetPath(snapshot.InstructionImagePath);
                        }
                        var id = await publisher.SendAsync(snapshot.ChannelId, pages[i], image, cancellationToken);
                        await repository.TransactAsync(state =>
                        {
                            if (state?.EventId != snapshot.EventId) return (state, false);
                            var current = state.Deliveries.Single(d => d.Id == delivery.Id);
                            current.MessageIds.Add(id);
                            if (current.PeriodId is { } periodId)
                                state.Periods.Single(p => p.PeriodId == periodId).AnnouncementMessageIds.Add(id);
                            current.Completed = current.MessageIds.Count == pages.Count;
                            return (state, true);
                        }, CancellationToken.None);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"Bingo public presentation failed ({ex.GetType().Name}); queued work remains for retry.");
                }
            }
            await UpdateReportAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private async Task UpdateReportAsync(CancellationToken cancellationToken)
    {
        var state = await repository.ReadAsync(cancellationToken);
        if (state is null || state.ReportChannelId == 0 || state.ReportChannelId == state.ChannelId) return;
        var pages = BingoText.Report(state);
        var fingerprint = state.EventId + string.Join("\n", pages);
        if (_reportFingerprint == fingerprint && clock.GetUtcNow() - _lastReportCheck < TimeSpan.FromMinutes(1)) return;
        for (int i = 0; i < Math.Max(pages.Count, state.AdministrativeReportMessageIds.Count); i++)
        {
            var text = i < pages.Count ? pages[i] : "Bingo report page no longer needed.";
            var id = i < state.AdministrativeReportMessageIds.Count ? state.AdministrativeReportMessageIds[i] : 0;
            if (id != 0 && await publisher.UpdateAsync(state.ReportChannelId, id, text, cancellationToken)) continue;
            var replacement = await publisher.SendAsync(state.ReportChannelId, text, null, cancellationToken);
            var index = i;
            await repository.TransactAsync(current =>
            {
                if (current?.EventId != state.EventId) return (current, false);
                if (index < current.AdministrativeReportMessageIds.Count) current.AdministrativeReportMessageIds[index] = replacement;
                else current.AdministrativeReportMessageIds.Add(replacement);
                return (current, true);
            }, CancellationToken.None);
        }
        _reportFingerprint = fingerprint;
        _lastReportCheck = clock.GetUtcNow();
    }
}
