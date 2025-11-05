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
        private readonly List<ulong> AdminRoles = [
            776164397160726620,  //Directors
            1002727659505205319, //Turks
            718919601768890419,  //Leadership
            1435693145588498603, //Testing Role
        ];

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
            var user = command.User as SocketGuildUser;
            switch (command.Data.Name)
            {
                case "start":
                    if (user!.Roles.Any(x => AdminRoles.Contains(x.Id)))
                        await _roundService.StartNewRoundAsync(command);
                    break;
            }
        }

        public async Task HandleCommandAsync(SocketMessage message)
        {
            var user = message.Author as SocketGuildUser;
            if (message.Author.IsBot) return;

            var content = message.CleanContent.ToLower();

            if (content.StartsWith('/')) return;

            //Infrastructure
            else if (content == "!setchannel" && user!.Roles.Any(x => AdminRoles.Contains(x.Id)))
                await _infrastructureService.SetChannelAsync(message);
            else if (content == "!removechannel" && user!.Roles.Any(x => AdminRoles.Contains(x.Id)))
                await _infrastructureService.RemoveChannelAsync(message);
            else if (content == "!setinfochannel" && user!.Roles.Any(x => AdminRoles.Contains(x.Id)))
                await _infrastructureService.SetInfoChannelAsync(message);

            //Checking Channel
            else if (!_botState.BotChannelIds.Contains(message.Channel.Id)) return;

            //Rolling
            else if (content == "!roll")
                await _roundService.RollAsync(message);
            else if (content == "!end" && user!.Roles.Any(x => AdminRoles.Contains(x.Id)))
                await _roundService.EndRoundAsync();
        }
    }
}
