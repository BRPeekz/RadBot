using Discord;
using Discord.WebSocket;
using RadBot.Constants;
using RadBot.Services;

namespace RadBot.Bot
{
    public class CommandsHandler(
        LotteryService lotteryService,
        InfrastructureService infrastructureService,
        VerificationService verificationService,
        BotState botState,
        DiscordSocketClient client)
    {
        private readonly LotteryService _lotteryService = lotteryService;
        private readonly InfrastructureService _infrastructureService = infrastructureService;
        private readonly VerificationService _verificationService = verificationService;
        private readonly BotState _botState = botState;
        private readonly DiscordSocketClient _client = client;

        public async Task RegisterCommandsAsync()
        {
            var guild = _client.GetGuild(_botState.GuildId);

            var startCmd = new SlashCommandBuilder()
            .WithName("start")
            .WithDescription("Start a lottery round.")
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName("lottery_mode")
                    .WithDescription("Choose the lottery mode.")
                    .WithRequired(true)
                    .WithType(ApplicationCommandOptionType.String)
                    .AddChoice("Standard", "Standard")
                    .AddChoice("Boss Raid", "BossRaid"))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName("timing")
                    .WithDescription("Choose how the round ends.")
                    .WithRequired(true)
                    .WithType(ApplicationCommandOptionType.String)
                    .AddChoice("Automatic", "auto")
                    .AddChoice("Manual", "manual"))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName("end_time")
                    .WithDescription("When the round should end (HH:mm EST). Required for Automatic timing.")
                    .WithRequired(false)
                    .WithType(ApplicationCommandOptionType.String))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName("roll_min")
                    .WithDescription("Minimum roll value (Boss Raid only, default: 1).")
                    .WithRequired(false)
                    .WithType(ApplicationCommandOptionType.Integer))
            .AddOption(
                new SlashCommandOptionBuilder()
                    .WithName("roll_max")
                    .WithDescription("Maximum roll value (Boss Raid only, default: 100).")
                    .WithRequired(false)
                    .WithType(ApplicationCommandOptionType.Integer));

            var setChannelCmd = new SlashCommandBuilder()
                .WithName("setchannel")
                .WithDescription("Sets the bot's main channel.");

            var setInfoChannelCmd = new SlashCommandBuilder()
                .WithName("setinfochannel")
                .WithDescription("Sets the bot's info channel.");

            await guild.CreateApplicationCommandAsync(startCmd.Build());
            await guild.CreateApplicationCommandAsync(setChannelCmd.Build());
            await guild.CreateApplicationCommandAsync(setInfoChannelCmd.Build());
        }

        public async Task HandleSlashCommandAsync(SocketSlashCommand command)
        {
            var user = command.User as SocketGuildUser;
            if (!IsAdmin(user))
            {
                await command.RespondAsync("You do not have permission to use this command.", ephemeral: true);
                return;
            }

            switch (command.Data.Name)
            {
                case "start":
                    await _lotteryService.StartNewRoundAsync(command);
                    break;
                case "setchannel":
                    await _infrastructureService.SetChannelAsync(command);
                    break;
                case "setinfochannel":
                    await _infrastructureService.SetInfoChannelAsync(command);
                    break;
            }
        }

        public async Task HandleCommandAsync(SocketMessage message)
        {
            if (message.Author.IsBot) return;

            var content = message.CleanContent.ToLower().Trim();

            // Verification tracking — always check regardless of channel
            if (content.StartsWith("!iam"))
                _verificationService.TrackVerificationAttempt(message);

            if (content.StartsWith('/')) return;
            if (_botState.BotChannelId != message.Channel.Id) return;

            if (LotteryService.TryParseRollCommand(content, out int min, out int max))
                await _lotteryService.RollAsync(message, min, max);
            else if (content == "!end" && IsAdmin(message.Author as SocketGuildUser))
                await _lotteryService.EndRoundAsync();
        }

        public async Task HandleMemberUpdatedAsync(SocketGuildUser before, SocketGuildUser after)
        {
            await _verificationService.HandleMemberUpdatedAsync(before, after);
        }

        private static bool IsAdmin(SocketGuildUser? user)
            => user?.Roles.Any(r => DiscordIds.AdminRoleIds.Contains(r.Id)) ?? false;
    }
}
