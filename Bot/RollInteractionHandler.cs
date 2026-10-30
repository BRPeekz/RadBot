using Discord;
using Discord.WebSocket;
using RadBot.Bingo;
using RadBot.Services;

namespace RadBot.Bot;

/// <summary>Routes the universal slash roll to the existing lottery or Bingo service.</summary>
public sealed class RollInteractionHandler(LotteryService lottery, BingoService bingo,
    BingoAnnouncementService announcements, BotState state)
{
    /// <summary>Builds the shared roll command with optional casual bounds.</summary>
    public static SlashCommandProperties BuildCommand() => new SlashCommandBuilder()
        .WithName("roll").WithDescription("Roll in the active game, or make a casual roll when no game is active.")
        .AddOption(new SlashCommandOptionBuilder().WithName("min").WithDescription("Casual rolls only: minimum, default 1.")
            .WithType(ApplicationCommandOptionType.Integer).WithMinValue(1).WithMaxValue(int.MaxValue - 1))
        .AddOption(new SlashCommandOptionBuilder().WithName("max").WithDescription("Casual rolls only: maximum, default 100.")
            .WithType(ApplicationCommandOptionType.Integer).WithMinValue(1).WithMaxValue(int.MaxValue - 1))
        .Build();

    /// <summary>Defers privately; only successful roll results are published to the game channel.</summary>
    public async Task HandleAsync(SocketSlashCommand command)
    {
        try
        {
            await command.DeferAsync(ephemeral: true);
            if (command.GuildId != state.GuildId)
            {
                await ReplyAsync(command, "Use this command in the configured server.");
                return;
            }
            var minimum = command.Data.Options.FirstOrDefault(o => o.Name == "min")?.Value as long?;
            var maximum = command.Data.Options.FirstOrDefault(o => o.Name == "max")?.Value as long?;
            if (!LotteryService.TryGetRollRange(minimum, maximum, out int min, out int max))
            {
                await ReplyAsync(command, "Invalid range. Use positive min/max values with min no greater than max.");
                return;
            }
            await bingo.RecoverAsync();
            var current = await bingo.ReadAsync();
            if (current?.Status != BingoEventStatus.Active)
            {
                if (command.Channel.Id != state.BotChannelId)
                {
                    await ReplyAsync(command, "Use /roll in the configured game channel.");
                    return;
                }
                await lottery.RollAsync(command, min, max);
                return;
            }
            if (command.Channel.Id != current.ChannelId)
            {
                await ReplyAsync(command, "Use /roll in the configured Bingo game channel.");
                return;
            }
            if (minimum is not null || maximum is not null)
            {
                await ReplyAsync(command, "Bingo uses numbers 1–9. Use /roll without min or max.");
                return;
            }
            var result = await bingo.RollAsync(current.EventId, command.User.Id);
            await ReplyAsync(command, (result.Duplicate ? "Roll consumed; this number was already in your collection. " : "Roll recorded. ") +
                (result.PendingClaim ? "BINGO available! Use /bingo to claim your prize." : "Use /numbers to view your collection."));
            try { await announcements.RefreshAsync(); }
            catch (Exception ex)
            { Console.Error.WriteLine($"Bingo roll presentation failed ({ex.GetType().Name}); queued work remains for retry."); }
        }
        catch (Exception ex)
        {
            var text = ex is BingoException ? ex.Message : "Rolling is temporarily unavailable. Ask staff to check the bot configuration.";
            if (ex is not BingoException) Console.Error.WriteLine($"Slash roll failed ({ex.GetType().Name}); no sensitive payload logged.");
            try
            {
                if (command.HasResponded) await ReplyAsync(command, text);
                else await command.RespondAsync(text, ephemeral: true, allowedMentions: AllowedMentions.None);
            }
            catch (Exception responseError)
            { Console.Error.WriteLine($"Slash roll response failed ({responseError.GetType().Name})."); }
        }
    }

    private static Task ReplyAsync(SocketSlashCommand command, string text) =>
        command.FollowupAsync(text, ephemeral: true, allowedMentions: AllowedMentions.None);
}
