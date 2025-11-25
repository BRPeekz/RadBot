using Discord.WebSocket;

namespace RadBot.Services;

public class BotState
{
    public ulong GuildId { get; set; } = 713788214284124160; // First Class
    //public ulong GuildId { get; set; } = 1243287909650399404; // Testing Guild
    public ulong BotChannelId { get; set; } = 0;
    public ulong BotInfoChannelId { get; set; } = 0;
    public DiscordSocketClient Client { get; set; } = null!;
}
