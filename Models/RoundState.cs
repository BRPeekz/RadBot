namespace RadBot.Models;

public class RoundState
{
    public bool IsActive { get; set; } = false;
    public DateTime? EndTimeUtc { get; set; }
    public ulong? SummaryMessageId { get; set; }
    public ulong? SummaryMessageInfoChannelId { get; set; }
    public List<Roll> Rolls { get; set; } = []; 
}

