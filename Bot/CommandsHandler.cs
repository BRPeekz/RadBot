using Discord;
using Discord.WebSocket;
using RadBot.Services;
using RadBot.Bingo;

namespace RadBot.Bot
{
    public class CommandsHandler(
        LotteryService lotteryService,
        InfrastructureService infrastructureService,
        VerificationService verificationService,
        BotState botState,
        DiscordSocketClient client,
        BingoInteractionHandler bingoHandler,
        BingoService bingoService,
        LotteryActivityGate activityGate,
        RollInteractionHandler rollHandler)
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
            .WithDefaultMemberPermissions((GuildPermission)0)
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
                .WithDefaultMemberPermissions((GuildPermission)0)
                .WithDescription("Sets the bot's main channel.");

            var setInfoChannelCmd = new SlashCommandBuilder()
                .WithName("setinfochannel")
                .WithDefaultMemberPermissions((GuildPermission)0)
                .WithDescription("Sets the bot's info channel.");

            await guild.CreateApplicationCommandAsync(startCmd.Build());
            await guild.CreateApplicationCommandAsync(setChannelCmd.Build());
            await guild.CreateApplicationCommandAsync(setInfoChannelCmd.Build());
            await guild.CreateApplicationCommandAsync(RollInteractionHandler.BuildCommand());
            await guild.CreateApplicationCommandAsync(BuildEndCommand());
            foreach (var command in await guild.GetApplicationCommandsAsync())
                if (command.Name == "iam" && command.Type == ApplicationCommandType.Slash)
                    await command.DeleteAsync();
            await bingoHandler.RegisterAsync();
        }

        /// <summary>Builds the slash command for ending a Standard or BossRaid round.</summary>
        public static SlashCommandProperties BuildEndCommand() => new SlashCommandBuilder()
            .WithDefaultMemberPermissions((GuildPermission)0)
            .WithName("end").WithDescription("End the current Standard or BossRaid round (bot administrators only).").Build();

        public async Task HandleSlashCommandAsync(SocketSlashCommand command)
        {
            if (command.GuildId != _botState.GuildId)
            {
                await command.RespondAsync("Use this command in the configured server.", ephemeral: true);
                return;
            }
            if (command.Data.Name == "roll")
            {
                await rollHandler.HandleAsync(command);
                return;
            }
            if (bingoHandler.Handles(command.Data.Name))
            {
                await bingoHandler.HandleAsync(command);
                return;
            }
            var user = command.User as SocketGuildUser;
            if (!IsAdmin(user))
            {
                await command.RespondAsync("You do not have permission to use this command.", ephemeral: true);
                return;
            }

            switch (command.Data.Name)
            {
                case "end":
                    await command.DeferAsync(ephemeral: true);
                    if (command.Channel.Id != _botState.BotChannelId)
                    {
                        await command.FollowupAsync("Use /end in the configured main game channel.", ephemeral: true);
                        break;
                    }
                    try
                    {
                        var ended = await _lotteryService.EndRoundAsync();
                        await command.FollowupAsync(ended ? "Round ended." : "No active Standard/BossRaid round. Use /bingo-admin end-event for Bingo.", ephemeral: true);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Round ending failed ({ex.GetType().Name}); no sensitive payload logged.");
                        await command.FollowupAsync("Could not finish the round. Ask staff to check the channel configuration.", ephemeral: true);
                    }
                    break;
                case "start":
                    try
                    {
                        if (!await activityGate.TryRunAsync(async () =>
                        {
                            if ((await bingoService.ReadAsync())?.Status == BingoEventStatus.Active)
                                await command.RespondAsync("End the Bingo event before starting a Standard/BossRaid round.", ephemeral: true);
                            else
                                await _lotteryService.StartNewRoundAsync(command);
                        }))
                            await command.RespondAsync("Another lottery activation is in progress. Try again shortly.", ephemeral: true);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Lottery activation failed ({ex.GetType().Name}); no sensitive payload logged.");
                        if (!command.HasResponded)
                            await command.RespondAsync("Lottery activation is unavailable. Check storage and channel configuration.", ephemeral: true);
                    }
                    break;
                case "setchannel":
                    await _infrastructureService.SetChannelAsync(command);
                    break;
                case "setinfochannel":
                    await _infrastructureService.SetInfoChannelAsync(command);
                    break;
            }
        }

        public async Task HandleMemberUpdatedAsync(SocketGuildUser before, SocketGuildUser after)
        {
            await _verificationService.HandleMemberUpdatedAsync(before, after);
        }

        private static bool IsAdmin(SocketGuildUser? user) => BotAuthorization.IsAdmin(user);

    }
}
