using System.Collections.Concurrent;

namespace RadBot.Bingo;

/// <summary>Short-lived server-owned confirmation context; opaque IDs never authorize a user on their own.</summary>
public sealed record BingoClaimFlow(Guid FlowId, Guid EventId, ulong UserId, DateTimeOffset ExpiresAtUtc, BingoCombination? Selected);

/// <summary>Validates ownership, timeout and event identity independently of Discord payloads.</summary>
public sealed class BingoClaimFlows(BingoService service, TimeProvider clock)
{
    private readonly ConcurrentDictionary<Guid, BingoClaimFlow> _flows = new();

    /// <summary>Creates a ten-minute flow, replacing stale flows for the same player.</summary>
    public async Task<(BingoClaimFlow Flow, IReadOnlyList<BingoPrize> Options)> OpenAsync(Guid eventId, ulong userId)
    {
        var options = await service.GetClaimOptionsAsync(eventId, userId);
        if (options.Count == 0) throw new BingoException("No complete, available Bingo line. Your eligibility has been recalculated; check /numbers or /bingo-status.");
        foreach (var pair in _flows.Where(p => p.Value.ExpiresAtUtc <= clock.GetUtcNow() || p.Value.UserId == userId)) _flows.TryRemove(pair.Key, out _);
        if (_flows.Count >= 4096) throw new BingoException("Too many active claim menus. Please try again shortly.");
        var flow = new BingoClaimFlow(Guid.NewGuid(), eventId, userId, clock.GetUtcNow().AddMinutes(10), options.Count == 1 ? options[0].Combination : null);
        _flows[flow.FlowId] = flow;
        return (flow, options);
    }

    /// <summary>Checks the opaque component context before interpreting any client-provided selection.</summary>
    public BingoClaimFlow Get(Guid flowId, ulong userId, Guid eventId)
    {
        if (!_flows.TryGetValue(flowId, out var flow) || flow.UserId != userId || flow.EventId != eventId || flow.ExpiresAtUtc <= clock.GetUtcNow())
            throw new BingoException("This menu is expired or belongs to another user/event. Use /bingo to open your own menu.");
        return flow;
    }

    /// <summary>Revalidates the chosen line against the latest state before showing confirmation.</summary>
    public async Task<(BingoClaimFlow Flow, BingoPrize Prize)> ChooseAsync(Guid flowId, ulong userId, Guid eventId, BingoCombination combination)
    {
        var flow = Get(flowId, userId, eventId);
        var options = await service.GetClaimOptionsAsync(eventId, userId);
        var prize = options.FirstOrDefault(p => p.Combination == combination)
            ?? throw new BingoException("That prize is no longer available. Use /bingo to refresh your options.");
        var updated = flow with { Selected = combination };
        if (!_flows.TryUpdate(flowId, updated, flow)) throw new BingoException("Menu changed. Use /bingo again.");
        return (updated, prize);
    }

    /// <summary>Final authorization and atomic game-state revalidation.</summary>
    public Task<BingoClaim> ConfirmAsync(Guid flowId, ulong userId, Guid eventId)
    {
        var flow = Get(flowId, userId, eventId);
        if (flow.Selected is not { } selected) throw new BingoException("Select one prize before confirming.");
        return service.ClaimAsync(eventId, userId, selected, flowId);
    }

    /// <summary>Cancels a menu, not the underlying obligation to claim an available prize.</summary>
    public void Cancel(Guid flowId, ulong userId, Guid eventId)
    {
        var flow = Get(flowId, userId, eventId);
        _flows.TryRemove(new KeyValuePair<Guid, BingoClaimFlow>(flowId, flow));
    }
}
