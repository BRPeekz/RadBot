using Discord.WebSocket;

namespace RadBot.Services;

public class BotState
{
    public ulong BotChannelId { get; set; } = 0;
    public ulong GuildId { get; set; } = 713788214284124160;
    public DiscordSocketClient Client { get; set; } = null!;
}
