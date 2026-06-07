namespace RadBot.Models;

public class RoundState
{
    public bool IsActive { get; set; } = false;
    public string LotteryModeName { get; set; } = string.Empty;
    public int RollMin { get; set; } = 1;
    public int RollMax { get; set; } = 100;
    public DateTime? EndTimeUtc { get; set; }
    public ulong? SummaryMessageId { get; set; }
    public ulong? SummaryMessageInfoChannelId { get; set; }
    public List<Roll> Rolls { get; set; } = [];
}

