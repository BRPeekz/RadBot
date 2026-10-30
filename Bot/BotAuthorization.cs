using Discord.WebSocket;
using RadBot.Constants;

namespace RadBot.Bot;

/// <summary>Existing role-based administrator policy shared by legacy and Bingo adapters.</summary>
public static class BotAuthorization
{
    /// <summary>Accepts exactly the administrator roles already configured by this bot.</summary>
    public static bool IsAdmin(SocketGuildUser? user) => user?.Roles.Any(r => DiscordIds.AdminRoleIds.Contains(r.Id)) ?? false;
}

/// <summary>Serializes activation of legacy rounds and multi-period Bingo events.</summary>
public sealed class LotteryActivityGate : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Runs a lifecycle activation check and action under one gate.</summary>
    public async Task RunAsync(Func<Task> action, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await action(); }
        finally { _gate.Release(); }
    }

    /// <summary>Attempts activation without waiting past a Discord interaction's acknowledgment deadline.</summary>
    public async Task<bool> TryRunAsync(Func<Task> action)
    {
        if (!await _gate.WaitAsync(0)) return false;
        try { await action(); return true; }
        finally { _gate.Release(); }
    }

    /// <summary>Releases the activation gate.</summary>
    public void Dispose() => _gate.Dispose();
}
