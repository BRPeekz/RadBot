using Discord.WebSocket;
using RadBot.Data;

namespace RadBot.Services
{
    public class InfrastructureService(BotState botState)
    {
        private readonly BotState _botState = botState;

        public async Task SetChannelAsync(SocketMessage message)
        {
            _botState.BotChannelId = message.Channel.Id;
            Storage.SaveInfrastructure(new Models.Infrastructure { BotChannelId = _botState.BotChannelId, BotInfoChannelId = _botState.BotInfoChannelId });
            await message.Channel.SendMessageAsync($"All set up and ready to go!");
        }

        public async Task SetInfoChannelAsync(SocketMessage message)
        {
            _botState.BotInfoChannelId = message.Channel.Id;
            Storage.SaveInfrastructure(new Models.Infrastructure { BotChannelId = _botState.BotChannelId, BotInfoChannelId = _botState.BotInfoChannelId });
            await message.Channel.SendMessageAsync($"All set up and ready to go!");
        }
    }
}
