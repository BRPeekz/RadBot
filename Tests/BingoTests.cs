using System.Net;
using RadBot.Bingo;
using RadBot.Bot;
using RadBot.Lottery;
using RadBot.Models;
using Xunit;

namespace RadBot.Tests;

public sealed class BingoTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "RadBotTests-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new();
    private readonly FixedRandom _random = new();
    private readonly HttpClient _http = new(new RejectNetwork());
    private readonly JsonBingoRepository _repository;
    private readonly BingoImageStore _images;
    private readonly BingoService _service;
    private Guid _eventId;
    private readonly string _image = Guid.NewGuid().ToString("N") + ".png";

    public BingoTests()
    {
        var options = new BingoOptions { DataDirectory = _directory };
        Directory.CreateDirectory(Path.Combine(_directory, "images"));
        File.WriteAllBytes(Path.Combine(_directory, "images", _image), [137, 80, 78, 71, 13, 10, 26, 10]);
        _repository = new JsonBingoRepository(options);
        _images = new BingoImageStore(options, _http);
        _service = new BingoService(_repository, _clock, _random, _images);
    }

    private BingoCreation Creation() => new("Test Bingo", 10, 20, _image,
        BingoRules.Lines.Keys.ToDictionary(k => k, k => "Prize " + BingoRules.Label(k)), TimeSpan.FromHours(24));
    private async Task CreateAsync() => _eventId = await _service.CreateAsync(Creation());
    private async Task<Guid> OpenAsync() => await _service.StartPeriodAsync(_eventId);
    private async Task OpenAutomaticSuccessorAsync(string announcement)
    {
        var first = await OpenAsync();
        await _service.ScheduleNextPeriodAsync(_eventId, first, announcement);
        await _service.ClosePeriodAsync(_eventId, first);
    }
    private async Task SeedAsync(ulong userId, params int[] numbers) => await _repository.TransactAsync(state =>
    {
        state!.Players[userId] = new BingoPlayerState { UserId = userId, UniqueNumbers = numbers.ToHashSet() };
        return (state, true);
    });
    private async Task NextPeriodAsync()
    {
        var state = (await _service.ReadAsync())!;
        await _service.ClosePeriodAsync(_eventId, state.Periods.Last().PeriodId);
        await OpenAsync();
    }

    [Fact]
    public void EightExactImmutableCombinations() => Assert.Equal(
        new[] { "123", "147", "159", "258", "357", "369", "456", "789" }, BingoRules.Lines.Keys.Select(BingoRules.Label).Order());

    [Fact]
    public async Task OrderDoesNotMatterAndOneLineDetected()
    {
        await CreateAsync(); await SeedAsync(1, 3, 1, 2);
        var prize = Assert.Single(await _service.GetClaimOptionsAsync(_eventId, 1));
        Assert.Equal(BingoCombination.RowTop, prize.Combination);
    }

    [Fact]
    public async Task RollWithoutEventIsRejected() => await Assert.ThrowsAsync<BingoException>(() => _service.RollAsync(Guid.NewGuid(), 1));

    [Fact]
    public async Task RollWithoutWindowIsRejected()
    {
        await CreateAsync();
        await Assert.ThrowsAsync<BingoException>(() => _service.RollAsync(_eventId, 1));
    }

    [Fact]
    public async Task DuplicateConsumesWindowAndRemainsInHistoryAcrossPeriods()
    {
        await CreateAsync(); await OpenAsync();
        _random.Value = 2;
        await _service.RollAsync(_eventId, 1);
        await Assert.ThrowsAsync<BingoException>(() => _service.RollAsync(_eventId, 1));
        await NextPeriodAsync();
        var result = await _service.RollAsync(_eventId, 1);
        Assert.True(result.Duplicate);
        var player = (await _service.ReadAsync())!.Players[1];
        Assert.Equal(2, player.Rolls.Count);
        Assert.Single(player.UniqueNumbers);
        Assert.Equal(2, player.Rolls.Select(r => r.PeriodId).Distinct().Count());
        await Assert.ThrowsAsync<BingoException>(() => _service.RollAsync(_eventId, 1));
    }

    [Fact]
    public async Task OneNumberCanCompleteMultipleLinesAndBlocksFutureRolls()
    {
        await CreateAsync(); await OpenAsync(); await SeedAsync(1, 2, 3, 4, 7);
        _random.Value = 1;
        Assert.True((await _service.RollAsync(_eventId, 1)).PendingClaim);
        Assert.Equal(2, (await _service.GetClaimOptionsAsync(_eventId, 1)).Count);
        await NextPeriodAsync();
        await Assert.ThrowsAsync<BingoException>(() => _service.RollAsync(_eventId, 1));
    }

    [Fact]
    public async Task ClaimingOneLineLeavesOtherPrizeAvailableAndWinnerCannotPlay()
    {
        await CreateAsync(); await OpenAsync(); await SeedAsync(1, 1, 2, 3, 4, 7);
        await _service.ClaimAsync(_eventId, 1, BingoCombination.RowTop, Guid.NewGuid());
        var state = (await _service.ReadAsync())!;
        Assert.Null(state.Prizes.Single(p => p.Combination == BingoCombination.ColumnLeft).Claim);
        Assert.False(BingoRules.IsEligible(state, state.Players[1]));
        await Assert.ThrowsAsync<BingoException>(() => _service.RollAsync(_eventId, 1));
        await Assert.ThrowsAsync<BingoException>(() => _service.ClaimAsync(_eventId, 1, BingoCombination.ColumnLeft, Guid.NewGuid()));
    }

    [Fact]
    public async Task ClaimedLinesAreExcludedAndPlayerUnblocksWhenAllOptionsAreGone()
    {
        await CreateAsync(); await OpenAsync();
        await SeedAsync(1, 1, 2, 3, 4, 7); await SeedAsync(2, 1, 2, 3); await SeedAsync(3, 1, 4, 7);
        await _service.ClaimAsync(_eventId, 2, BingoCombination.RowTop, Guid.NewGuid());
        Assert.Single(await _service.GetClaimOptionsAsync(_eventId, 1));
        await _service.ClaimAsync(_eventId, 3, BingoCombination.ColumnLeft, Guid.NewGuid());
        Assert.Empty(await _service.GetClaimOptionsAsync(_eventId, 1));
        var state = (await _service.ReadAsync())!;
        Assert.True(BingoRules.IsEligible(state, state.Players[1]));
        _random.Value = 9;
        await _service.RollAsync(_eventId, 1);
    }

    [Fact]
    public async Task ConcurrentClaimsAwardExactlyOneWinner()
    {
        await CreateAsync(); await SeedAsync(1, 1, 2, 3); await SeedAsync(2, 1, 2, 3);
        async Task<bool> Claim(ulong id)
        {
            try { await _service.ClaimAsync(_eventId, id, BingoCombination.RowTop, Guid.NewGuid()); return true; }
            catch (BingoException) { return false; }
        }
        var results = await Task.WhenAll(Claim(1), Claim(2));
        Assert.Single(results, r => r);
        var state = (await _service.ReadAsync())!;
        Assert.Single(state.Players.Values, p => p.HasWon);
        Assert.Single(state.Prizes, p => p.Claim is not null);
    }

    [Fact]
    public async Task SameClaimFlowIsIdempotent()
    {
        await CreateAsync(); await SeedAsync(1, 1, 2, 3);
        var id = Guid.NewGuid();
        var first = await _service.ClaimAsync(_eventId, 1, BingoCombination.RowTop, id);
        Assert.Equal(first, await _service.ClaimAsync(_eventId, 1, BingoCombination.RowTop, id));
        Assert.Single((await _service.ReadAsync())!.Deliveries);
    }

    [Fact]
    public async Task FinalConfirmationRejectsPrizeClaimedAfterMenuWasShown()
    {
        await CreateAsync(); await SeedAsync(1, 1, 2, 3); await SeedAsync(2, 1, 2, 3);
        var flows = new BingoClaimFlows(_service, _clock);
        var (flow, _) = await flows.OpenAsync(_eventId, 1);
        await _service.ClaimAsync(_eventId, 2, BingoCombination.RowTop, Guid.NewGuid());
        await Assert.ThrowsAsync<BingoException>(() => flows.ConfirmAsync(flow.FlowId, 1, _eventId));
        Assert.False((await _service.ReadAsync())!.Players[1].HasWon);
    }

    [Fact]
    public async Task FlowChecksOwnerEventTimeoutAndRestart()
    {
        await CreateAsync(); await SeedAsync(1, 1, 2, 3);
        var flows = new BingoClaimFlows(_service, _clock);
        var (flow, _) = await flows.OpenAsync(_eventId, 1);
        Assert.Throws<BingoException>(() => flows.Get(flow.FlowId, 2, _eventId));
        Assert.Throws<BingoException>(() => flows.Get(flow.FlowId, 1, Guid.NewGuid()));
        Assert.Throws<BingoException>(() => new BingoClaimFlows(_service, _clock).Get(flow.FlowId, 1, _eventId));
        _clock.Advance(TimeSpan.FromMinutes(11));
        await Assert.ThrowsAsync<BingoException>(() => flows.ConfirmAsync(flow.FlowId, 1, _eventId));
    }

    [Fact]
    public async Task MultipleOptionsRequireSelectionAndCancelDoesNotUnblock()
    {
        await CreateAsync(); await OpenAsync(); await SeedAsync(1, 1, 2, 3, 4, 7);
        var flows = new BingoClaimFlows(_service, _clock);
        var (flow, options) = await flows.OpenAsync(_eventId, 1);
        Assert.Equal(2, options.Count); Assert.Null(flow.Selected);
        await Assert.ThrowsAsync<BingoException>(() => flows.ConfirmAsync(flow.FlowId, 1, _eventId));
        await Assert.ThrowsAsync<BingoException>(() => flows.ChooseAsync(flow.FlowId, 1, _eventId, BingoCombination.RowBottom));
        flows.Cancel(flow.FlowId, 1, _eventId);
        await Assert.ThrowsAsync<BingoException>(() => _service.RollAsync(_eventId, 1));
        var (replacement, _) = await flows.OpenAsync(_eventId, 1);
        await flows.ChooseAsync(replacement.FlowId, 1, _eventId, BingoCombination.ColumnLeft);
        Assert.Equal(BingoCombination.ColumnLeft, (await flows.ConfirmAsync(replacement.FlowId, 1, _eventId)).Combination);
    }

    [Fact]
    public async Task PersistedStateReloadsAndRestoresFuturePeriod()
    {
        await CreateAsync(); var period = await OpenAsync(); await _service.RollAsync(_eventId, 1);
        using var repo = new JsonBingoRepository(new BingoOptions { DataDirectory = _directory });
        var restarted = new BingoService(repo, _clock, _random, _images);
        await restarted.RecoverAsync();
        var state = (await restarted.ReadAsync())!;
        Assert.Equal(period, state.Periods.Single().PeriodId);
        Assert.Equal(BingoPeriodStatus.Open, state.Periods.Single().Status);
        Assert.Single(state.Players[1].Rolls);
        await Assert.ThrowsAsync<BingoException>(() => restarted.RollAsync(_eventId, 1));
    }

    [Fact]
    public async Task ExpiredPeriodClosesOnRestartIdempotently()
    {
        await CreateAsync(); await OpenAsync();
        _clock.Advance(TimeSpan.FromHours(25));
        var restarted = new BingoService(_repository, _clock, _random, _images);
        await restarted.RecoverAsync();
        var first = (await restarted.ReadAsync())!.Periods.Single().ClosedAtUtc;
        _clock.Advance(TimeSpan.FromHours(1)); await restarted.RecoverAsync();
        Assert.Equal(first, (await restarted.ReadAsync())!.Periods.Single().ClosedAtUtc);
        await Assert.ThrowsAsync<BingoException>(() => restarted.RollAsync(_eventId, 1));
    }

    [Fact]
    public async Task ManualAndScheduledClosureDoNotCloseNextPeriod()
    {
        await CreateAsync(); var first = await OpenAsync();
        _clock.Advance(TimeSpan.FromHours(25));
        await Task.WhenAll(_service.RecoverAsync(), _service.ClosePeriodAsync(_eventId, first));
        var second = await OpenAsync();
        Assert.False(await _service.ClosePeriodAsync(_eventId, first));
        Assert.Equal(BingoPeriodStatus.Open, (await _service.ReadAsync())!.Periods.Single(p => p.PeriodId == second).Status);
    }

    [Fact]
    public async Task SecondEventAndSecondWindowRejectedUnderConcurrency()
    {
        await CreateAsync();
        await Assert.ThrowsAsync<BingoException>(() => _service.CreateAsync(Creation()));
        async Task<bool> Open()
        {
            try { await OpenAsync(); return true; } catch (BingoException) { return false; }
        }
        Assert.Single(await Task.WhenAll(Open(), Open()), r => r);
    }

    [Fact]
    public async Task EighthClaimEndsEventClosesWindowAndRemainsIdempotent()
    {
        await CreateAsync(); await OpenAsync();
        var lastFlow = Guid.NewGuid();
        foreach (var combination in BingoRules.Lines.Keys.Order())
        {
            ulong user = (ulong)combination + 1;
            await SeedAsync(user, BingoRules.Lines[combination].ToArray());
            await _service.ClaimAsync(_eventId, user, combination, combination == BingoCombination.DiagonalUp ? lastFlow : Guid.NewGuid());
        }
        var state = (await _service.ReadAsync())!;
        Assert.Equal(BingoEventStatus.Ended, state.Status);
        Assert.Equal(BingoPeriodStatus.Closed, state.Periods.Single().Status);
        Assert.Equal(8, state.Players.Values.Count(p => p.HasWon));
        Assert.Equal(lastFlow, (await _service.ClaimAsync(_eventId, 8, BingoCombination.DiagonalUp, lastFlow)).ClaimId);
        Assert.False(await _service.EndAsync(_eventId));
        Assert.Equal(9, state.Deliveries.Count);
    }

    [Fact]
    public async Task EndEventClosesWindowAndArchivesWhenNextEventIsCreated()
    {
        await CreateAsync(); await OpenAsync(); var old = _eventId;
        await _service.EndAsync(old);
        await RefreshAsync();
        await CreateAsync();
        Assert.NotEqual(old, _eventId);
        Assert.True(File.Exists(Path.Combine(_directory, $"bingo-{old:N}.json")));
        await Assert.ThrowsAsync<BingoException>(() => _service.RollAsync(old, 1));
    }

    [Fact]
    public async Task MissingJsonMeansNoEventAndInvalidJsonIsNeverOverwritten()
    {
        Assert.Null(await _repository.ReadAsync());
        var path = Path.Combine(_directory, "bingo-event.json");
        await File.WriteAllTextAsync(path, "not-json");
        await Assert.ThrowsAsync<InvalidDataException>(() => _repository.ReadAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.CreateAsync(Creation()));
        Assert.Equal("not-json", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task UnsupportedSchemaIsPreserved()
    {
        await CreateAsync();
        var path = Path.Combine(_directory, "bingo-event.json");
        var invalid = (await File.ReadAllTextAsync(path)).Replace("\"Version\": 1", "\"Version\": 999");
        await File.WriteAllTextAsync(path, invalid);
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.EndAsync(_eventId));
        Assert.Equal(invalid, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task SnapshotMutationDoesNotChangePersistedStateAndBackupExists()
    {
        await CreateAsync(); await OpenAsync();
        var state = (await _repository.ReadAsync())!; state.Name = "not persisted";
        Assert.Equal("Test Bingo", (await _repository.ReadAsync())!.Name);
        Assert.True(File.Exists(Path.Combine(_directory, "bingo-event.json.bak")));
    }

    [Fact]
    public async Task ManualStartWorksWithoutImageAndAutomaticSchedulingRequiresIt()
    {
        await CreateAsync(); var first = await OpenAsync();
        Assert.Empty((await _service.ReadAsync())!.Deliveries);
        File.Delete(_images.GetPath(_image));
        await Assert.ThrowsAsync<BingoException>(() => _service.ScheduleNextPeriodAsync(_eventId, first, "Instructions"));
        await _service.ClosePeriodAsync(_eventId, first);
        await OpenAsync();
        var state = (await _service.ReadAsync())!;
        Assert.Equal(BingoPeriodMode.Manual, state.Periods.Last().Mode);
        Assert.Empty(state.Deliveries);
    }

    private async Task<FakePublisher> RefreshAsync(FakePublisher? publisher = null)
    {
        publisher ??= new FakePublisher(_repository);
        await new BingoAnnouncementService(_repository, publisher, _images, _clock).RefreshAsync();
        return publisher;
    }

    [Fact]
    public async Task AutomaticAnnouncementReusesImageAndReportIsPrivateAndDeletedPageRecreated()
    {
        await CreateAsync(); await OpenAutomaticSuccessorAsync(new string('A', 2200) + "\nEnd");
        await SeedAsync(1, 1, 2);
        var publisher = await RefreshAsync();
        var state = (await _service.ReadAsync())!;
        Assert.True(state.Deliveries.Single().Completed);
        Assert.Equal(2, state.Periods.Last().AnnouncementMessageIds.Count);
        Assert.Single(publisher.Messages.Values, m => m.Image is not null);
        Assert.DoesNotContain(publisher.Messages.Values.Where(m => m.Channel == 10), m => m.Text.Contains("STAFF") || m.Text.Contains("Participants:"));
        var reportId = Assert.Single(state.AdministrativeReportMessageIds);
        publisher.Messages.Remove(reportId);
        await RefreshAsync(publisher);
        Assert.NotEqual(reportId, Assert.Single((await _service.ReadAsync())!.AdministrativeReportMessageIds));
    }

    [Fact]
    public async Task FailedPublicDeliveryKeepsClaimAndRetriesAfterRestart()
    {
        await CreateAsync(); await SeedAsync(1, 1, 2, 3);
        await _service.ClaimAsync(_eventId, 1, BingoCombination.RowTop, Guid.NewGuid());
        var publisher = new FakePublisher(_repository) { FailPublic = true };
        await RefreshAsync(publisher);
        Assert.True((await _service.ReadAsync())!.Players[1].HasWon);
        Assert.False((await _service.ReadAsync())!.Deliveries.Single().Completed);
        publisher.FailPublic = false;
        var scheduler = new BingoScheduler(_service, new BingoAnnouncementService(_repository, publisher, _images, _clock), _clock);
        await scheduler.TickAsync(); await scheduler.TickAsync();
        Assert.Single(publisher.Messages.Values, m => m.Channel == 10);
        Assert.True((await _service.ReadAsync())!.Deliveries.Single().Completed);
        scheduler.Dispose();
    }

    [Fact]
    public async Task RollPublicTextNeverContainsPrivateCollectionOrPendingLines()
    {
        await CreateAsync(); await OpenAsync(); await SeedAsync(1, 2, 3, 4, 7); _random.Value = 1;
        await _service.RollAsync(_eventId, 1);
        Assert.Equal("<@1> rolled 🎲 **1**!", Assert.Single((await _service.ReadAsync())!.Deliveries).Text);
    }

    [Fact]
    public void PagesPreserveContentAndSurrogatePairs()
    {
        var text = new string('A', 1899) + "🎲" + new string('B', 3000) + "\nEnd";
        var pages = BingoText.Pages(text);
        Assert.Equal(text, string.Concat(pages));
        Assert.All(pages, p => { Assert.True(p.Length <= 1900); Assert.False(char.IsHighSurrogate(p[^1])); });
    }

    [Theory]
    [InlineData("../image.png")]
    [InlineData("C:\\private.png")]
    [InlineData("../../file.png")]
    [InlineData("not-generated.png")]
    public void StoredImagePathRejectsTraversal(string path) => Assert.Throws<BingoException>(() => _images.GetPath(path));

    [Theory]
    [InlineData("https://example.com/image.png", "image.png", "image/png", 100)]
    [InlineData("https://cdn.discordapp.com/image.svg", "image.svg", "image/svg+xml", 100)]
    [InlineData("https://cdn.discordapp.com/image.png", "image.png", "text/html", 100)]
    [InlineData("https://cdn.discordapp.com/image.png", "image.png", "image/png", 9000000)]
    public async Task InvalidAttachmentsNeverReachNetwork(string url, string name, string mime, long size) =>
        await Assert.ThrowsAsync<BingoException>(() => _images.SaveAsync(url, name, mime, size));

    [Fact]
    public async Task ActivationGateSerializesCompetingActions()
    {
        using var gate = new LotteryActivityGate();
        int active = 0;
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => gate.RunAsync(async () =>
        {
            Assert.Equal(1, Interlocked.Increment(ref active)); await Task.Yield(); Interlocked.Decrement(ref active);
        })));
    }

    [Fact]
    public void LegacyModesRetainSingleRollAndResultBehavior()
    {
        var rolls = new List<Roll> { new() { UserId = 1, Value = 50 }, new() { UserId = 2, Value = 90 } };
        var standard = new StandardLotteryMode(); var boss = new BossRaidLotteryMode(1, 100);
        Assert.False(standard.CanUserRoll(1, rolls)); Assert.False(boss.CanUserRoll(1, rolls));
        Assert.Equal((ulong)2, Assert.Single(standard.DetermineResult(rolls)!.Winners).UserId);
        Assert.False(boss.DeclaresWinner); Assert.Empty(boss.DetermineResult(rolls)!.Winners);
        Assert.Contains("140", boss.BuildSummaryText(rolls));
    }

    [Theory]
    [InlineData("EventId")]
    [InlineData("Version")]
    [InlineData("Status")]
    [InlineData("Periods")]
    [InlineData("Players")]
    [InlineData("Prizes")]
    [InlineData("Deliveries")]
    public async Task MissingRequiredFieldsCannotRegenerateOrReactivateState(string field)
    {
        await CreateAsync();
        var path = Path.Combine(_directory, "bingo-event.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        json.Remove(field);
        var invalid = json.ToJsonString();
        await File.WriteAllTextAsync(path, invalid);
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ReadAsync());
        Assert.Equal(invalid, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("Periods")]
    [InlineData("Prizes")]
    [InlineData("Deliveries")]
    public async Task NullCollectionElementsFailClosed(string field)
    {
        await CreateAsync(); await OpenAsync(); await _service.RollAsync(_eventId, 1);
        var path = Path.Combine(_directory, "bingo-event.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        json[field]!.AsArray()[0] = null;
        await File.WriteAllTextAsync(path, json.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.ReadAsync());
    }

    [Fact]
    public async Task InconsistentWinnerCannotReturnToPlayAfterReload()
    {
        await CreateAsync(); await OpenAsync(); await SeedAsync(1, 1, 2, 3);
        await _service.ClaimAsync(_eventId, 1, BingoCombination.RowTop, Guid.NewGuid());
        var path = Path.Combine(_directory, "bingo-event.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        json["Players"]!["1"]!["WinningClaim"] = null;
        await File.WriteAllTextAsync(path, json.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.RollAsync(_eventId, 1));
    }

    [Fact]
    public async Task MissingImageDeliveryDoesNotBlockPublicRolls()
    {
        await CreateAsync(); await OpenAutomaticSuccessorAsync("Instructions");
        File.Delete(_images.GetPath(_image));
        await _service.RollAsync(_eventId, 1);
        var publisher = await RefreshAsync();
        Assert.Single(publisher.Messages.Values, m => m.Channel == 10 && m.Text == "<@1> rolled 🎲 **1**!");
        var deliveries = (await _service.ReadAsync())!.Deliveries;
        Assert.False(deliveries[0].Completed); Assert.True(deliveries[1].Completed);
    }

    [Fact]
    public async Task CancellationAfterPublicSendStillCommitsDeliveryAcknowledgment()
    {
        await CreateAsync(); await OpenAsync(); await _service.RollAsync(_eventId, 1);
        using var cancellation = new CancellationTokenSource();
        var publisher = new FakePublisher(_repository) { OnSent = cancellation.Cancel };
        var announcements = new BingoAnnouncementService(_repository, publisher, _images, _clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => announcements.RefreshAsync(cancellation.Token));
        Assert.True((await _service.ReadAsync())!.Deliveries.Single().Completed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImageDownloadValidatesContentAndUsesGeneratedFilename(bool validSignature)
    {
        byte[] content = validSignature ? [137, 80, 78, 71, 13, 10, 26, 10] : [1, 2, 3, 4, 5, 6, 7, 8];
        using var client = new HttpClient(new ImageResponse(content));
        var store = new BingoImageStore(new BingoOptions { DataDirectory = _directory }, client);
        Task<string> Download() => store.SaveAsync("https://cdn.discordapp.com/attachments/test.png", "../../uploaded.png", "image/png", content.Length);
        if (!validSignature)
        {
            await Assert.ThrowsAsync<BingoException>(Download);
            return;
        }
        var name = await Download();
        Assert.True(Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out _));
        Assert.Equal(content, await File.ReadAllBytesAsync(store.GetPath(name)));
        Assert.DoesNotContain("uploaded", name);
    }

    [Fact]
    public async Task EndedEventWithUndeliveredWorkIsArchivedWithoutBlockingNewEvent()
    {
        await CreateAsync(); var old = _eventId;
        await _service.EndAsync(old);
        await CreateAsync();
        var archive = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_directory, $"bingo-{old:N}.json")))!;
        Assert.False(archive["Deliveries"]![0]!["Completed"]!.GetValue<bool>());
        Assert.Equal(old, archive["EventId"]!.GetValue<Guid>());
        Assert.NotEqual(old, _eventId);
    }

    [Theory]
    [InlineData(0UL, 20UL)]
    [InlineData(10UL, 10UL)]
    public async Task MissingGameChannelAndPublicStaffChannelAreRejected(ulong gameChannel, ulong staffChannel) =>
        await Assert.ThrowsAsync<BingoException>(() => _service.CreateAsync(Creation() with { ChannelId = gameChannel, ReportChannelId = staffChannel }));

    [Fact]
    public async Task UnconfiguredStaffChannelNeverFallsBackToPublicChannel()
    {
        _eventId = await _service.CreateAsync(Creation() with { ReportChannelId = 0 });
        Assert.Empty((await RefreshAsync()).Messages);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unopened")]
    [InlineData("closed")]
    [InlineData("expired")]
    [InlineData("ended")]
    public async Task SchedulingRequiresAnActiveEventAndLivePeriod(string scenario)
    {
        var previousId = Guid.NewGuid();
        if (scenario != "missing") await CreateAsync();
        if (scenario is "closed" or "expired" or "ended") previousId = await OpenAsync();
        if (scenario == "closed") await _service.ClosePeriodAsync(_eventId, previousId);
        if (scenario == "expired") _clock.Advance(TimeSpan.FromHours(24));
        if (scenario == "ended") await _service.EndAsync(_eventId);
        await Assert.ThrowsAsync<BingoException>(() => _service.GetSchedulingPeriodIdAsync(_eventId));
        await Assert.ThrowsAsync<BingoException>(() => _service.ScheduleNextPeriodAsync(_eventId, previousId, "Next announcement"));
        Assert.Null((await _service.ReadAsync())?.NextPeriod);
    }

    [Fact]
    public async Task AutomaticSuccessorRetainsInputsAndOnlyPublishesAfterCurrentPeriodEnds()
    {
        await CreateAsync(); var first = await OpenAsync();
        await _service.RollAsync(_eventId, 1);
        await SeedAsync(2, 1, 2, 3);
        await _service.ClaimAsync(_eventId, 2, BingoCombination.RowTop, Guid.NewGuid());
        var text = new string('A', 2200) + "\nSaved next-period announcement";
        await _service.ScheduleNextPeriodAsync(_eventId, first, text);
        var scheduled = (await _service.ReadAsync())!;
        Assert.Single(scheduled.Periods);
        Assert.Equal(text, scheduled.NextPeriod!.Announcement);
        Assert.DoesNotContain(scheduled.Deliveries, d => d.Text == text);
        Assert.DoesNotContain("Next period", BingoText.Status(scheduled, 1));
        Assert.Contains("Next period #2 scheduled", string.Concat(BingoText.Report(scheduled)));
        await _repository.TransactAsync(state => { state!.DefaultPeriodDuration = TimeSpan.FromHours(12); return (state, true); });

        _clock.Advance(TimeSpan.FromHours(24));
        var publisher = new FakePublisher(_repository);
        using var scheduler = new BingoScheduler(_service, new BingoAnnouncementService(_repository, publisher, _images, _clock), _clock);
        await scheduler.TickAsync(); await scheduler.TickAsync();
        var current = (await _service.ReadAsync())!;
        Assert.Null(current.NextPeriod);
        Assert.Equal(2, current.Periods.Count);
        Assert.Equal(BingoPeriodStatus.Closed, current.Periods[0].Status);
        var next = current.Periods[1];
        Assert.Equal(BingoPeriodStatus.Open, next.Status);
        Assert.Equal(BingoPeriodMode.Automatic, next.Mode);
        Assert.Equal(TimeSpan.FromHours(24), next.EndUtc - next.StartUtc);
        Assert.Empty(next.RolledUsers);
        Assert.Equal(new[] { 1 }, current.Players[1].UniqueNumbers);
        Assert.Single(current.Players[1].Rolls);
        Assert.True(current.Players[2].HasWon);
        Assert.NotNull(current.Prizes.Single(p => p.Combination == BingoCombination.RowTop).Claim);
        Assert.Single(publisher.Messages.Values, m => m.Image == _images.GetPath(_image));
        Assert.Equal(text, string.Concat(next.AnnouncementMessageIds.Select(id => publisher.Messages[id].Text)));
        Assert.True((await _service.RollAsync(_eventId, 1)).Duplicate);
        await Assert.ThrowsAsync<BingoException>(() => _service.RollAsync(_eventId, 2));
    }

    [Fact]
    public async Task AutomaticSuccessorStartsOnEarlyCloseAndQueuesItsAnnouncement()
    {
        await CreateAsync(); var first = await OpenAsync();
        await _service.ScheduleNextPeriodAsync(_eventId, first, "Automatic successor");
        _clock.Advance(TimeSpan.FromHours(1));
        Assert.True(await _service.ClosePeriodAsync(_eventId, first));
        var state = (await _service.ReadAsync())!;
        Assert.Null(state.NextPeriod);
        Assert.Equal(2, state.Periods.Count);
        Assert.Equal(_clock.GetUtcNow(), state.Periods[1].StartUtc);
        Assert.Equal(BingoPeriodMode.Automatic, state.Periods[1].Mode);
        Assert.Equal("Automatic successor", Assert.Single(state.Deliveries).Text);
        Assert.False(await _service.ClosePeriodAsync(_eventId, first));
        Assert.Equal(BingoPeriodStatus.Open, (await _service.ReadAsync())!.Periods[1].Status);
    }

    [Fact]
    public async Task SuccessorSurvivesRestartAndStartsOnceWithFullDurationAfterDowntime()
    {
        await CreateAsync(); var first = await OpenAsync();
        await _service.ScheduleNextPeriodAsync(_eventId, first, "Saved through restart");
        using var repository = new JsonBingoRepository(new BingoOptions { DataDirectory = _directory });
        var restarted = new BingoService(repository, _clock, _random, _images);
        await restarted.RecoverAsync();
        Assert.Single((await restarted.ReadAsync())!.Periods);
        Assert.NotNull((await restarted.ReadAsync())!.NextPeriod);
        _clock.Advance(TimeSpan.FromDays(3));
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => restarted.RecoverAsync()));
        var state = (await restarted.ReadAsync())!;
        Assert.Equal(2, state.Periods.Count);
        Assert.Null(state.NextPeriod);
        Assert.Equal(_clock.GetUtcNow(), state.Periods[1].StartUtc);
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromHours(24), state.Periods[1].EndUtc);
        Assert.Equal("Saved through restart", Assert.Single(state.Deliveries).Text);
        _clock.Advance(TimeSpan.FromHours(25)); await restarted.RecoverAsync();
        state = (await restarted.ReadAsync())!;
        Assert.Equal(2, state.Periods.Count);
        Assert.Equal(BingoPeriodStatus.Closed, state.Periods[1].Status);
    }

    [Fact]
    public async Task ConcurrentManualAndScheduledClosureStartExactlyOneSuccessor()
    {
        await CreateAsync(); var first = await OpenAsync();
        await _service.ScheduleNextPeriodAsync(_eventId, first, "Only once");
        _clock.Advance(TimeSpan.FromHours(25));
        await Task.WhenAll(_service.RecoverAsync(), _service.RecoverAsync(), _service.ClosePeriodAsync(_eventId, first));
        var state = (await _service.ReadAsync())!;
        Assert.Equal(2, state.Periods.Count);
        Assert.Single(state.Periods, p => p.Status == BingoPeriodStatus.Open);
        Assert.Single(state.Deliveries, d => d.Text == "Only once");
        Assert.Null(state.NextPeriod);
    }

    [Fact]
    public async Task ConcurrentSchedulingCannotReplaceTheFirstSavedInputs()
    {
        await CreateAsync(); var first = await OpenAsync();
        async Task<string?> Schedule(string text)
        {
            try { await _service.ScheduleNextPeriodAsync(_eventId, first, text); return text; }
            catch (BingoException) { return null; }
        }
        var results = await Task.WhenAll(Schedule("First input"), Schedule("Second input"));
        var saved = Assert.Single(results, r => r is not null);
        Assert.Equal(saved, (await _service.ReadAsync())!.NextPeriod!.Announcement);
        await Assert.ThrowsAsync<BingoException>(() => _service.GetSchedulingPeriodIdAsync(_eventId));
    }

    [Fact]
    public async Task LateModalCannotScheduleAgainstADifferentPeriod()
    {
        await CreateAsync(); var first = await _service.StartPeriodAsync(_eventId);
        Assert.Equal(first, await _service.GetSchedulingPeriodIdAsync(_eventId));
        await _service.ClosePeriodAsync(_eventId, first); await OpenAsync();
        await Assert.ThrowsAsync<BingoException>(() => _service.ScheduleNextPeriodAsync(_eventId, first, "Stale modal"));
        Assert.Null((await _service.ReadAsync())!.NextPeriod);
    }

    [Fact]
    public async Task EndingAnExpiredEventCancelsItsSuccessorWithoutStartingIt()
    {
        await CreateAsync(); var first = await OpenAsync();
        await _service.ScheduleNextPeriodAsync(_eventId, first, "Must not open");
        _clock.Advance(TimeSpan.FromHours(25));
        await _service.EndAsync(_eventId); await _service.RecoverAsync();
        var state = (await _service.ReadAsync())!;
        Assert.Null(state.NextPeriod);
        Assert.Single(state.Periods);
        Assert.Equal(BingoEventStatus.Ended, state.Status);
        Assert.DoesNotContain(state.Deliveries, d => d.Text == "Must not open");
    }

    [Fact]
    public async Task EighthPrizeCancelsTheScheduledSuccessor()
    {
        await CreateAsync(); var first = await OpenAsync();
        await _service.ScheduleNextPeriodAsync(_eventId, first, "Cancelled by final prize");
        foreach (var combination in BingoRules.Lines.Keys.Order())
        {
            ulong user = (ulong)combination + 1;
            await SeedAsync(user, BingoRules.Lines[combination].ToArray());
            await _service.ClaimAsync(_eventId, user, combination, Guid.NewGuid());
        }
        await _service.RecoverAsync();
        var state = (await _service.ReadAsync())!;
        Assert.Equal(BingoEventStatus.Ended, state.Status);
        Assert.Null(state.NextPeriod);
        Assert.Single(state.Periods);
        Assert.DoesNotContain(state.Deliveries, d => d.Text == "Cancelled by final prize");
    }

    [Fact]
    public async Task OlderJsonWithoutSuccessorPropertyRemainsCompatibleAndDoesNotInventOne()
    {
        await CreateAsync(); await OpenAsync();
        var path = Path.Combine(_directory, "bingo-event.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        json.Remove("NextPeriod"); await File.WriteAllTextAsync(path, json.ToJsonString());
        Assert.Null((await _service.ReadAsync())!.NextPeriod);
        _clock.Advance(TimeSpan.FromHours(25)); await _service.RecoverAsync();
        var state = (await _service.ReadAsync())!;
        Assert.Equal(1, state.Version);
        Assert.Single(state.Periods);
        Assert.Equal(BingoPeriodStatus.Closed, state.Periods[0].Status);
    }

    [Fact]
    public async Task MalformedSuccessorFailsClosedWithoutOverwritingJson()
    {
        await CreateAsync(); var first = await OpenAsync();
        await _service.ScheduleNextPeriodAsync(_eventId, first, "Saved text");
        var path = Path.Combine(_directory, "bingo-event.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        json["NextPeriod"]!["PreviousPeriodId"] = Guid.NewGuid().ToString();
        var invalid = json.ToJsonString(); await File.WriteAllTextAsync(path, invalid);
        await Assert.ThrowsAsync<InvalidDataException>(() => _service.RecoverAsync());
        Assert.Equal(invalid, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task MissingImageAfterSchedulingDoesNotPreventTheDurableTransition()
    {
        await CreateAsync(); var first = await OpenAsync();
        await _service.ScheduleNextPeriodAsync(_eventId, first, "Image temporarily missing");
        File.Delete(_images.GetPath(_image)); _clock.Advance(TimeSpan.FromHours(24));
        await _service.RecoverAsync();
        var state = (await _service.ReadAsync())!;
        Assert.Equal(2, state.Periods.Count);
        Assert.Equal(BingoPeriodStatus.Open, state.Periods[1].Status);
        Assert.False(Assert.Single(state.Deliveries).Completed);
        await _service.RollAsync(_eventId, 1);
    }

    [Fact]
    public void PeriodCommandsDoNotExposeAModeSelector()
    {
        var commands = BingoInteractionHandler.BuildAdminCommand().Options.Value;
        var start = Assert.Single(commands, c => c.Name == "start-period");
        var next = Assert.Single(commands, c => c.Name == "open-next-period");
        Assert.Empty(start.Options ?? []);
        Assert.Empty(next.Options ?? []);
        Assert.DoesNotContain(commands, c => c.Name == "open-period");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task AutomaticSchedulingRequiresAnnouncementText(string? announcement)
    {
        await CreateAsync(); var first = await OpenAsync();
        await Assert.ThrowsAsync<BingoException>(() => _service.ScheduleNextPeriodAsync(_eventId, first, announcement!));
        var state = (await _service.ReadAsync())!;
        Assert.Null(state.NextPeriod);
        Assert.Single(state.Periods);
        Assert.Equal(BingoPeriodMode.Manual, state.Periods[0].Mode);
        Assert.Empty(state.Deliveries);
    }

    [Fact]
    public async Task AutomaticSchedulingRejectsOversizedAnnouncement()
    {
        await CreateAsync(); var first = await OpenAsync();
        await Assert.ThrowsAsync<BingoException>(() => _service.ScheduleNextPeriodAsync(_eventId, first, new string('A', 4001)));
        Assert.Null((await _service.ReadAsync())!.NextPeriod);
    }

    [Fact]
    public async Task PreviouslyPersistedManualSuccessorIsNotDiscardedOnUpgrade()
    {
        await CreateAsync(); var first = await OpenAsync();
        await _repository.TransactAsync(state =>
        {
            state!.NextPeriod = new BingoNextPeriodState
            {
                PreviousPeriodId = first, Mode = BingoPeriodMode.Manual,
                Announcement = null, Duration = state.DefaultPeriodDuration
            };
            return (state, true);
        });
        Assert.Equal(BingoPeriodMode.Manual, (await _service.ReadAsync())!.NextPeriod!.Mode);
        await Assert.ThrowsAsync<BingoException>(() => _service.ScheduleNextPeriodAsync(_eventId, first, "Do not replace saved inputs"));
        await _service.ClosePeriodAsync(_eventId, first);
        var state = (await _service.ReadAsync())!;
        Assert.Equal(2, state.Periods.Count);
        Assert.Equal(BingoPeriodMode.Manual, state.Periods[1].Mode);
        Assert.Null(state.NextPeriod);
        Assert.Empty(state.Deliveries);
    }

    [Theory]
    [InlineData(null, null, 1, 100)]
    [InlineData(null, 20L, 1, 20)]
    [InlineData(10L, 20L, 10, 20)]
    [InlineData(10L, null, 10, 100)]
    [InlineData(7L, 7L, 7, 7)]
    [InlineData(1L, 2147483646L, 1, 2147483646)]
    public void SlashRollBoundsPreserveCasualDefaultsAndRanges(long? minimum, long? maximum, int expectedMin, int expectedMax)
    {
        Assert.True(RadBot.Services.LotteryService.TryGetRollRange(minimum, maximum, out int min, out int max));
        Assert.Equal(expectedMin, min);
        Assert.Equal(expectedMax, max);
    }

    [Theory]
    [InlineData(0L, 100L)]
    [InlineData(-1L, 100L)]
    [InlineData(10L, 5L)]
    [InlineData(null, 0L)]
    [InlineData(null, 2147483647L)]
    [InlineData(null, long.MaxValue)]
    [InlineData(long.MaxValue, long.MaxValue)]
    public void SlashRollBoundsRejectInvalidOrOverflowingRanges(long? minimum, long? maximum) =>
        Assert.False(RadBot.Services.LotteryService.TryGetRollRange(minimum, maximum, out _, out _));

    [Fact]
    public void UniversalSlashRollHasOnlyOptionalIntegerBounds()
    {
        var command = RollInteractionHandler.BuildCommand();
        Assert.Equal("roll", command.Name.Value);
        var options = command.Options.Value;
        Assert.Equal(new[] { "max", "min" }, options.Select(o => o.Name).Order());
        Assert.All(options, option =>
        {
            Assert.Equal(Discord.ApplicationCommandOptionType.Integer, option.Type);
            Assert.True(option.IsRequired is not true);
        });
        using var gate = new LotteryActivityGate();
        var handler = new BingoInteractionHandler(_service, new BingoClaimFlows(_service, _clock), _images,
            new BingoAnnouncementService(_repository, new FakePublisher(_repository), _images, _clock), gate,
            new RadBot.Services.BotState(), _clock);
        Assert.False(handler.Handles("roll"));
        Assert.True(handler.Handles("bingo"));
    }

    [Fact]
    public void EndCommandHasSlashContract()
    {
        var end = CommandsHandler.BuildEndCommand();
        Assert.Equal("end", end.Name.Value);
        Assert.True(end.DefaultMemberPermissions.IsSpecified);
        Assert.Equal((Discord.GuildPermission)0, end.DefaultMemberPermissions.Value);
        var bingoAdmin = BingoInteractionHandler.BuildAdminCommand();
        Assert.True(bingoAdmin.DefaultMemberPermissions.IsSpecified);
        Assert.Equal((Discord.GuildPermission)0, bingoAdmin.DefaultMemberPermissions.Value);
        Assert.False(end.Options.IsSpecified && end.Options.Value.Any());
    }

    [Fact]
    public async Task PlayerStatusDoesNotRevealWhetherASuccessorIsScheduled()
    {
        await CreateAsync(); var first = await OpenAsync();
        var before = BingoText.Status((await _service.ReadAsync())!, 1);
        await _service.ScheduleNextPeriodAsync(_eventId, first, "Staff-only future announcement");
        var state = (await _service.ReadAsync())!;
        Assert.Equal(before, BingoText.Status(state, 1));
        Assert.DoesNotContain("Staff-only future announcement", BingoText.Status(state, 1));
        Assert.Contains("Next period #2 scheduled", string.Concat(BingoText.Report(state)));
    }

    public void Dispose()
    {
        _repository.Dispose(); _http.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
    private sealed class FixedRandom : IBingoRandom
    {
        public int Value { get; set; } = 1;
        public int Next() => Value;
    }
    private sealed class RejectNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Tests must not access the network.");
    }
    private sealed class ImageResponse(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return Task.FromResult(response);
        }
    }
    private sealed class FakePublisher(IBingoRepository repository) : IBingoPublisher
    {
        public Dictionary<ulong, (ulong Channel, string Text, string? Image)> Messages { get; } = [];
        public bool FailPublic { get; set; }
        public Action? OnSent { get; init; }
        private ulong _next = 100;
        public async Task<ulong> SendAsync(ulong channelId, string text, string? imagePath, CancellationToken cancellationToken)
        {
            Assert.NotNull(await repository.ReadAsync(cancellationToken));
            if (channelId == 10 && FailPublic) throw new IOException("Simulated missing channel.");
            var id = _next++; Messages[id] = (channelId, text, imagePath); OnSent?.Invoke(); return id;
        }
        public Task<bool> UpdateAsync(ulong channelId, ulong messageId, string text, CancellationToken cancellationToken)
        {
            if (!Messages.ContainsKey(messageId)) return Task.FromResult(false);
            Messages[messageId] = (channelId, text, null); return Task.FromResult(true);
        }
    }
}
