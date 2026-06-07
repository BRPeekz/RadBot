using RadBot.Models;

namespace RadBot.Lottery;

/// <summary>
/// Boss Raid mode: players join together to deal damage to a boss.
/// Roll range is configurable (default 1-100). No winner is declared;
/// total damage is announced at the end of the rolling period.
/// </summary>
public class BossRaidLotteryMode(int rollMin = 1, int rollMax = 100) : ILotteryMode
{
    private static readonly Random Rng = new();

    public int RollMin { get; } = rollMin;
    public int RollMax { get; } = rollMax;

    public string ModeName => "BossRaid";

    public bool DeclaresWinner => false;

    public int GenerateRoll() => Rng.Next(RollMin, RollMax + 1);

    public bool CanUserRoll(ulong userId, List<Roll> existingRolls)
        => !existingRolls.Any(r => r.UserId == userId);

    public string BuildSummaryText(List<Roll> rolls)
    {
        var lines = rolls.Select(r => $"<@{r.UserId}> — **{r.Value}**");
        var text = string.Join("\n", lines);

        var totalDamage = rolls.Sum(r => r.Value);
        text += $"\n\n💥 **Total Damage:** {totalDamage}";

        return text;
    }

    public LotteryResult? DetermineResult(List<Roll> rolls)
    {
        if (rolls.Count == 0) return null;
        return new LotteryResult { Winners = [] };
    }
}
