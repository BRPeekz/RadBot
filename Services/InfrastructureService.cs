using Discord.WebSocket;
using RadBot.Data;

namespace RadBot.Services
{
    public class InfrastructureService(BotState botState)
    {
        private readonly BotState _botState = botState;

        public async Task SetChannelAsync(SocketSlashCommand command)
        {
            _botState.BotChannelId = command.Channel.Id;
            Storage.SaveInfrastructure(new Models.Infrastructure { BotChannelId = _botState.BotChannelId, BotInfoChannelId = _botState.BotInfoChannelId });
            await command.RespondAsync($"All set up and ready to go!", ephemeral: true);
        }

        public async Task SetInfoChannelAsync(SocketSlashCommand command)
        {
            _botState.BotInfoChannelId = command.Channel.Id;
            Storage.SaveInfrastructure(new Models.Infrastructure { BotChannelId = _botState.BotChannelId, BotInfoChannelId = _botState.BotInfoChannelId });
            await command.RespondAsync($"All set up and ready to go!", ephemeral: true);
        }
    }
}
