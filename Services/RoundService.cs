using Discord;
using Discord.WebSocket;
using RadBot.Data;
using RadBot.Models;

namespace RadBot.Services
{
    public class RoundService(BotState state)
    {
        private readonly BotState _botState = state;
        private readonly RoundState _roundState = Storage.LoadRound();
        private CancellationTokenSource? _roundCts;

        private static readonly Random Rng = new();


        public void ScheduleEndRound(DateTime? endTime)
        {
            _roundCts?.Cancel();
            _roundCts = new CancellationTokenSource();

            if (endTime == null)
                return;

            var delay = endTime.Value - DateTime.UtcNow;
            if (delay < TimeSpan.Zero)
                delay = TimeSpan.Zero;

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

            var mode = (string)command.Data.Options.First(x => x.Name == "mode").Value;
            if (mode != "auto" && mode != "manual")
            {
                await command.RespondAsync("❌ Valid values: `auto` or `manual`.");
                return;
            }

            if (mode == "auto")
            {
                var timeStr = (string)command.Data.Options.First(o => o.Name == "end_time").Value;
                if (!TimeSpan.TryParse(timeStr, out TimeSpan endTimeEst))
                {
                    await command.RespondAsync("❌ Invalid time format. Use `HH:mm` (example: `19:30`).");
                    return;
                }

                TimeZoneInfo tz;
                try
                {
                    // Windows
                    tz = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                }
                catch
                {
                    // Linux
                    tz = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
                }

                // Agora local da rodada (hoje na data deles)
                var nowEst = TimeZoneInfo.ConvertTime(DateTime.UtcNow, tz);
                var roundEndEst = nowEst.Date + endTimeEst;

                // Se a hora já passou hoje → agendar para amanhã
                if (roundEndEst <= nowEst)
                    roundEndEst = roundEndEst.AddDays(1);

                // Converter pra UTC para salvar
                var endUtc = TimeZoneInfo.ConvertTimeToUtc(roundEndEst, tz);

                _roundState.EndTimeUtc = endUtc;
                Storage.SaveRound(_roundState);

                ScheduleEndRound(endUtc);
            }

            _roundState.IsActive = true;

            _roundState.Rolls.Clear();
            Storage.SaveRound(_roundState);

            await command.RespondAsync($"✅ Round started in mode **{mode}**.");
        }

        public async Task EndRoundAsync()
        {
            var channel = _botState.Client.GetChannel(_botState.BotChannelId) as IMessageChannel;

            if (!_roundState.IsActive)
            {
                await channel!.SendMessageAsync("Uhh, nothing to end here.");
                return;
            }

            if (_roundState.Rolls.Count == 0)
            {
                await channel!.SendMessageAsync("No rolls were made this round. No winner!");
                _roundState.IsActive = false;
                Storage.SaveRound(_roundState);
                return;
            }

            var winner = _roundState.Rolls.OrderByDescending(r => r.Value).First();

            //check for ties
            var tiedWinners = _roundState.Rolls.Where(x => x.Value == winner.Value).ToList();
            if (tiedWinners.Count > 1)
            {
                var message = "Oh, we have a tie!\n" +
                    "The players:\n\n";

                foreach (var tiedWinner in tiedWinners)
                    message += $"<@{tiedWinner.UserId}>\n";

                message += $"\nHave tied with a {winner.Value} roll.";

                await channel!.SendMessageAsync(message);
            }
            else
            {
                await channel!.SendMessageAsync($"🏆 Winner: <@{winner.UserId}> with a {winner.Value} roll!");
            }

            _roundState.IsActive = false;
            _roundState.EndTimeUtc = null;
            Storage.SaveRound(_roundState);
        }

        public async Task RollAsync(SocketMessage message)
        {
            if (!_roundState.IsActive)
            {
                await message.Channel.SendMessageAsync($"<@{message.Author.Id}> rolled 🎲 **{Rng.Next(0, 101)}**!\n(Out of a rolling period)");
                return;
            }

            if (_roundState.Rolls.Any(x => x.UserId == message.Author.Id))
            {
                await message.Channel.SendMessageAsync("Can't roll twice! Wait for your next chance");
                return;
            }

            var roll = new Roll()
            {
                UserId = message.Author.Id,
                Value = Rng.Next(0, 101),
                Time = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "E. South America Standard Time")
            };

            _roundState.Rolls.Add(roll);
            Storage.SaveRound(_roundState);

            await message.Channel.SendMessageAsync($"<@{message.Author.Id}> rolled 🎲 **{roll.Value}**!");
        }
    }
}
