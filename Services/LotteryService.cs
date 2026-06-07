using Discord;
using Discord.WebSocket;
using RadBot.Data;
using RadBot.Lottery;
using RadBot.Models;
using System.Text.RegularExpressions;

namespace RadBot.Services;

public partial class LotteryService(BotState state)
{
    private readonly BotState _botState = state;
    private RoundState _roundState = Storage.LoadRound();
    private CancellationTokenSource? _roundCts;

    private ILotteryMode? _mode;
    private static readonly Random CasualRng = new();

    [GeneratedRegex(@"^!roll\s*$")]
    private static partial Regex PlainRollRegex();

    [GeneratedRegex(@"^!roll\s+(\d+)\s*$")]
    private static partial Regex SingleMaxRollRegex();

    [GeneratedRegex(@"^!roll\s*(\d+)\s*-\s*(\d+)\s*$")]
    private static partial Regex RangeRollRegex();

    /// <summary>
    /// Tries to parse a roll command. Returns true if the message is a !roll variant.
    /// </summary>
    public static bool TryParseRollCommand(string content, out int min, out int max)
    {
        min = 1;
        max = 100;

        if (PlainRollRegex().IsMatch(content))
            return true;

        var rangeMatch = RangeRollRegex().Match(content);
        if (rangeMatch.Success)
        {
            min = int.Parse(rangeMatch.Groups[1].Value);
            max = int.Parse(rangeMatch.Groups[2].Value);
            return true;
        }

        var singleMatch = SingleMaxRollRegex().Match(content);
        if (singleMatch.Success)
        {
            max = int.Parse(singleMatch.Groups[1].Value);
            return true;
        }

        return false;
    }

    private static ILotteryMode ResolveMode(RoundState roundState)
    {
        return roundState.LotteryModeName switch
        {
            "BossRaid" => new BossRaidLotteryMode(roundState.RollMin, roundState.RollMax),
            _ => new StandardLotteryMode()
        };
    }

    public void ScheduleEndRound(DateTime? endTime)
    {
        _roundCts?.Cancel();
        _roundCts = new CancellationTokenSource();

        if (endTime == null) return;

        var delay = endTime.Value - DateTime.UtcNow;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _roundCts.Token);
                await EndRoundAsync();
            }
            catch (TaskCanceledException) { }
        });
    }

    public async Task StartNewRoundAsync(SocketSlashCommand command)
    {
        if (_roundState.IsActive)
        {
            await command.RespondAsync("We're already rolling!\n(You need to end it first :p)");
            return;
        }

        // Reset round.json to avoid schema conflicts
        _roundState = Storage.ResetRound();

        var timing = (string)command.Data.Options.First(x => x.Name == "timing").Value;
        if (timing != "auto" && timing != "manual")
        {
            await command.RespondAsync("❌ Valid values: `auto` or `manual`.");
            return;
        }

        if (timing == "auto")
        {
            var timeStr = (string)command.Data.Options.First(o => o.Name == "end_time").Value;
            if (!TimeSpan.TryParse(timeStr, out TimeSpan endTimeEst))
            {
                await command.RespondAsync("❌ Invalid time format. Use `HH:mm` (example: `19:30`).");
                return;
            }

            var tz = GetEasternTimeZone();
            var nowEst = TimeZoneInfo.ConvertTime(DateTime.UtcNow, tz);
            var roundEndEst = nowEst.Date + endTimeEst;
            roundEndEst = roundEndEst.AddDays(1);

            var endUtc = TimeZoneInfo.ConvertTimeToUtc(roundEndEst, tz);
            _roundState.EndTimeUtc = endUtc;

            ScheduleEndRound(endUtc);
        }

        // Determine lottery mode
        var lotteryMode = (string)command.Data.Options.First(x => x.Name == "lottery_mode").Value;
        _roundState.LotteryModeName = lotteryMode;

        if (lotteryMode == "BossRaid")
        {
            var rollMinOpt = command.Data.Options.FirstOrDefault(x => x.Name == "roll_min");
            var rollMaxOpt = command.Data.Options.FirstOrDefault(x => x.Name == "roll_max");
            _roundState.RollMin = rollMinOpt != null ? Convert.ToInt32(rollMinOpt.Value) : 1;
            _roundState.RollMax = rollMaxOpt != null ? Convert.ToInt32(rollMaxOpt.Value) : 100;
        }

        _mode = ResolveMode(_roundState);

        var channel = _botState.Client.GetChannel(_botState.BotChannelId) as IMessageChannel;
        var infoChannel = _botState.Client.GetChannel(_botState.BotInfoChannelId) as IMessageChannel;

        var description = lotteryMode == "BossRaid"
            ? $"No rolls yet.\n⚔️ Roll range: **{_roundState.RollMin}–{_roundState.RollMax}**"
            : "No rolls yet.";

        var embed = new EmbedBuilder()
            .WithTitle($"🎲 Round Started! ({_mode.ModeName})")
            .WithDescription(description)
            .WithColor(Color.Blue)
            .Build();

        var msg = await channel!.SendMessageAsync(embed: embed);
        var infoMsg = await infoChannel!.SendMessageAsync(embed: embed);

        _roundState.SummaryMessageId = msg.Id;
        _roundState.SummaryMessageInfoChannelId = infoMsg.Id;
        _roundState.IsActive = true;
        _roundState.Rolls.Clear();
        Storage.SaveRound(_roundState);

        await command.RespondAsync($"✅ Round started — **{_mode.ModeName}** mode, timing: **{timing}**.", ephemeral: true);
    }

    public async Task EndRoundAsync()
    {
        var channel = _botState.Client.GetChannel(_botState.BotChannelId) as IMessageChannel;

        if (!_roundState.IsActive)
        {
            await channel!.SendMessageAsync("Uhh, nothing to end here.");
            return;
        }

        _mode ??= ResolveMode(_roundState);

        await UpdateRoundSummaryAsync(isEndOfTheRound: true);

        var result = _mode.DetermineResult(_roundState.Rolls);

        if (result == null)
        {
            await channel!.SendMessageAsync("No rolls were made this round.");
        }
        else if (_mode.DeclaresWinner)
        {
            if (result.IsTie)
            {
                var msg = "Oh, we have a tie!\nThe players:\n\n";
                foreach (var winner in result.Winners)
                    msg += $"<@{winner.UserId}>\n";
                msg += $"\nHave tied with a {result.Winners[0].Value} roll.";
                await channel!.SendMessageAsync(msg);
            }
            else
            {
                var winner = result.Winners[0];
                await channel!.SendMessageAsync($"🏆 Winner: <@{winner.UserId}> with a {winner.Value} roll!");
            }
        }
        else
        {
            var totalDamage = _roundState.Rolls.Sum(r => r.Value);
            await channel!.SendMessageAsync($"⚔️ Round over! Total damage dealt: **{totalDamage}**");
        }

        _roundState.IsActive = false;
        _roundState.LotteryModeName = string.Empty;
        _roundState.EndTimeUtc = null;
        _roundState.SummaryMessageId = null;
        _roundState.SummaryMessageInfoChannelId = null;
        _mode = null;
        Storage.SaveRound(_roundState);
    }

    public async Task RollAsync(SocketMessage message, int casualMin, int casualMax)
    {
        if (!_roundState.IsActive)
        {
            // Custom range only allowed outside a rolling period
            var casualRoll = CasualRng.Next(casualMin, casualMax + 1);
            var rangeLabel = casualMin == 1 ? $"{casualMax}" : $"{casualMin}–{casualMax}";
            await message.Channel.SendMessageAsync(
                $"<@{message.Author.Id}> rolled 🎲 **{casualRoll}** (1–{rangeLabel})!\n(Out of a rolling period)");
            return;
        }

        // During an active round, custom ranges are not allowed
        if (casualMin != 1 || casualMax != 100)
        {
            await message.DeleteAsync();
            // Send an ephemeral-like message that auto-deletes
            var warning = await message.Channel.SendMessageAsync(
                $"<@{message.Author.Id}> Custom roll ranges are not allowed during an active rolling period. Use `!roll` only.");
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(8));
                try { await warning.DeleteAsync(); } catch { }
            });
            return;
        }

        _mode ??= ResolveMode(_roundState);

        if (!_mode.CanUserRoll(message.Author.Id, _roundState.Rolls))
        {
            await message.Channel.SendMessageAsync("Can't roll twice! Wait for your next chance");
            return;
        }

        var tz = GetEasternTimeZone();

        var roll = new Roll
        {
            UserId = message.Author.Id,
            Value = _mode.GenerateRoll(),
            Time = TimeZoneInfo.ConvertTime(DateTime.UtcNow, tz)
        };

        _roundState.Rolls.Add(roll);
        Storage.SaveRound(_roundState);

        await UpdateRoundSummaryAsync(isEndOfTheRound: false);
        await message.Channel.SendMessageAsync($"<@{message.Author.Id}> rolled 🎲 **{roll.Value}**!");
    }

    private async Task UpdateRoundSummaryAsync(bool isEndOfTheRound)
    {
        if (_botState.Client.GetChannel(_botState.BotChannelId) is not IMessageChannel channel ||
            _botState.Client.GetChannel(_botState.BotInfoChannelId) is not IMessageChannel infoChannel)
            return;

        if (_roundState.SummaryMessageId is null || _roundState.SummaryMessageInfoChannelId is null)
            return;

        if (await channel.GetMessageAsync(_roundState.SummaryMessageId.Value) is not IUserMessage msg)
            return;

        if (await infoChannel.GetMessageAsync(_roundState.SummaryMessageInfoChannelId.Value) is not IUserMessage infoMsg)
            return;

        _mode ??= ResolveMode(_roundState);

        var text = _mode.BuildSummaryText(_roundState.Rolls);

        if (isEndOfTheRound)
        {
            var embed = new EmbedBuilder()
                .WithTitle("🏁 Round Ended!")
                .WithDescription(text)
                .WithColor(Color.DarkGrey)
                .Build();

            await channel.SendMessageAsync(embed: embed);
            await msg.DeleteAsync();
            await infoMsg.ModifyAsync(m => m.Embed = embed);
        }
        else
        {
            var embed = new EmbedBuilder()
                .WithTitle($"🎲 Current Round ({_mode.ModeName})")
                .WithDescription(text)
                .WithColor(Color.Gold)
                .Build();

            await msg.ModifyAsync(m => m.Embed = embed);
            await infoMsg.ModifyAsync(m => m.Embed = embed);
        }
    }

    private static TimeZoneInfo GetEasternTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
        catch
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }
    }
}
