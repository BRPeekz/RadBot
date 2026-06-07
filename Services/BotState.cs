using Discord.WebSocket;
using RadBot.Constants;

namespace RadBot.Services;

public class BotState
{
    public ulong GuildId { get; set; } = DiscordIds.GuildId;
    public ulong BotChannelId { get; set; } = 0;
    public ulong BotInfoChannelId { get; set; } = 0;
    public DiscordSocketClient Client { get; set; } = null!;
}
