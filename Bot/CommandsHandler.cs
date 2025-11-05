using Discord;
using Discord.WebSocket;
using RadBot.Services;

namespace RadBot.Bot
{
    public class CommandsHandler(
        RoundService roundService,
        InfrastructureService infrastructureService,
        BotState botState,
        DiscordSocketClient client)
    {
        private readonly RoundService _roundService = roundService;
        private readonly InfrastructureService _infrastructureService = infrastructureService;
        private readonly BotState _botState = botState;
        private readonly DiscordSocketClient _client = client;

        public async Task RegisterCommandsAsync()
        {
            var guild = _client.GetGuild(_botState.GuildId);

            var command = new SlashCommandBuilder()
            .WithName("start")
            .WithDescription("Start a lottery round.")
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName("mode")
                    .WithDescription("Choose how the round resets.")
                    .WithRequired(true)
                    .WithType(ApplicationCommandOptionType.String)
                    .AddChoice("Automatic", "auto")
                    .AddChoice("Manual", "manual")
            ).AddOption(
                new SlashCommandOptionBuilder()
                    .WithName("end_time")
                    .WithDescription("When the round should end (HH:mm EST).")
                    .WithRequired(false)
                    .WithType(ApplicationCommandOptionType.String)
            ); ;

            await guild.CreateApplicationCommandAsync(command.Build());
        }

        public async Task HandleSlashCommandAsync(SocketSlashCommand command)
        {
            switch (command.Data.Name)
            {
                case "start":
                    await _roundService.StartNewRoundAsync(command);
                    break;
            }
        }


        public async Task HandleCommandAsync(SocketMessage message)
        {
            if (message.Author.IsBot) return;

            var content = message.CleanContent.ToLower();

            if (content.StartsWith('/')) return;

            //Infrastructure
            else if (content == "!setchannel")
                await _infrastructureService.SetChannelAsync(message);

            //Checking Channel
            else if (message.Channel.Id != _botState.BotChannelId) return;

            //Rolling
            else if (content == "!roll")
                await _roundService.RollAsync(message);
            else if (content == "!end")
                await _roundService.EndRoundAsync();
        }
    }
}
