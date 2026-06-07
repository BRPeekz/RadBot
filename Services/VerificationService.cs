using Discord.WebSocket;
using RadBot.Constants;
using System.Collections.Concurrent;

namespace RadBot.Services;

public class VerificationService(BotState botState)
{
    private readonly BotState _botState = botState;

    // Tracks users who sent !iam — stores userId -> original nickname at message time
    private readonly ConcurrentDictionary<ulong, PendingVerification> _pending = new();

    private static readonly TimeSpan VerificationTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Called when a message is sent in the verification channel.
    /// Records the user's current nickname so we can detect a change.
    /// </summary>
    public void TrackVerificationAttempt(SocketMessage message)
    {
        if (message.Channel.Id != DiscordIds.VerificationChannelId) return;
        if (message.Author.IsBot) return;

        var content = message.Content.Trim();
        if (!content.StartsWith("!iam ", StringComparison.OrdinalIgnoreCase)) return;

        var guildUser = message.Author as SocketGuildUser;
        if (guildUser == null) return;

        var currentNick = guildUser.Nickname ?? guildUser.Username;

        _pending[guildUser.Id] = new PendingVerification
        {
            OriginalNickname = currentNick,
            Timestamp = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Called on GuildMemberUpdated. If a tracked user's nickname changed,
    /// assign Recruit role and remove Unverified role.
    /// </summary>
    public async Task HandleMemberUpdatedAsync(SocketGuildUser before, SocketGuildUser after)
    {
        if (!_pending.TryGetValue(after.Id, out var pending)) return;

        // Clean up stale entries
        if (DateTime.UtcNow - pending.Timestamp > VerificationTimeout)
        {
            _pending.TryRemove(after.Id, out _);
            return;
        }

        var oldNick = before.Nickname ?? before.Username;
        var newNick = after.Nickname ?? after.Username;

        // Nickname hasn't changed — nothing to do yet
        if (oldNick == newNick) return;

        // Nickname changed and it was different from what it was when they sent !iam
        if (newNick != pending.OriginalNickname)
        {
            _pending.TryRemove(after.Id, out _);

            var guild = _botState.Client.GetGuild(DiscordIds.GuildId);
            var recruitRole = guild.GetRole(DiscordIds.RecruitRoleId);
            var unverifiedRole = guild.GetRole(DiscordIds.UnverifiedRoleId);

            if (recruitRole != null)
                await after.AddRoleAsync(recruitRole);

            if (unverifiedRole != null && after.Roles.Any(r => r.Id == DiscordIds.UnverifiedRoleId))
                await after.RemoveRoleAsync(unverifiedRole);
        }
    }

    private class PendingVerification
    {
        public required string OriginalNickname { get; init; }
        public required DateTime Timestamp { get; init; }
    }
}
