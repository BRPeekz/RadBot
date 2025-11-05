using Discord.WebSocket;
using RadBot.Data;

namespace RadBot.Services
{
    public class InfrastructureService(BotState botState)
    {
        private readonly BotState _botState = botState;

        public async Task SetChannelAsync(SocketMessage message)
        {
            _botState.BotChannelIds.Add(message.Channel.Id);
            Storage.SaveInfrastructure(new Models.Infrastructure { BotChannelId = _botState.BotChannelIds, BotInfoChannelId = _botState.BotInfoChannelId });
            await message.Channel.SendMessageAsync($"All set up and ready to go!");
        }

        public async Task RemoveChannelAsync(SocketMessage message)
        {
            _botState.BotChannelIds.Remove(message.Channel.Id);
            Storage.SaveInfrastructure(new Models.Infrastructure { BotChannelId = _botState.BotChannelIds, BotInfoChannelId = _botState.BotInfoChannelId });
            await message.Channel.SendMessageAsync($"Oh, k then, bye!");
        }

        public async Task SetInfoChannelAsync(SocketMessage message)
        {
            _botState.BotInfoChannelId = message.Channel.Id;
            Storage.SaveInfrastructure(new Models.Infrastructure { BotChannelId = _botState.BotChannelIds, BotInfoChannelId = _botState.BotInfoChannelId });
            await message.Channel.SendMessageAsync($"All set up and ready to go!");
        }
    }
}
