using Discord.WebSocket;
using RadBot.Data;

namespace RadBot.Services
{
    public class InfrastructureService(BotState botState)
    {
        private readonly BotState _botState = botState;

        public async Task SetChannelAsync(SocketMessage message)
        {
            Storage.SaveInfrastructure(new Models.Infrastructure { BotChannelId = message.Channel.Id });
            await message.Channel.SendMessageAsync($"All set up and ready to go!");
            _botState.BotChannelId = message.Channel.Id;
        }
    }
}
