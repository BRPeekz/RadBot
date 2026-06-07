using RadBot.Models;

namespace RadBot.Lottery;

public class LotteryResult
{
    public required List<Roll> Winners { get; init; }
    public bool IsTie => Winners.Count > 1;
}
