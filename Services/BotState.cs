using Discord.WebSocket;

namespace RadBot.Services;

public class BotState
{
    public ulong BotChannelId { get; set; } = 0;
    public ulong GuildId { get; set; } = 1243287909650399404;
    public DiscordSocketClient Client { get; set; } = null!;
}
