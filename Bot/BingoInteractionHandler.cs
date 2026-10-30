using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using RadBot.Bingo;
using RadBot.Data;
using RadBot.Services;

namespace RadBot.Bot;

/// <summary>Discord command/component adapter. Rules and durable state changes live in BingoService.</summary>
public sealed class BingoInteractionHandler(BingoService service, BingoClaimFlows flows, BingoImageStore images,
    BingoAnnouncementService announcements, LotteryActivityGate activityGate, BotState botState, TimeProvider clock)
{
    private sealed record ModalContext(Guid EventId, ulong UserId, DateTimeOffset ExpiresAtUtc, Guid PreviousPeriodId);
    private readonly ConcurrentDictionary<Guid, ModalContext> _modals = new();
    private static readonly HashSet<string> CommandNames = ["bingo", "numbers", "bingo-status", "bingo-admin"];

    /// <summary>Identifies commands owned by this adapter before the legacy admin guard runs.</summary>
    public bool Handles(string name) => CommandNames.Contains(name);

    /// <summary>Registers player commands and an administrative group using Discord.Net subcommands.</summary>
    public async Task RegisterAsync()
    {
        var guild = botState.Client.GetGuild(botState.GuildId);
        foreach (var (name, description) in new[]
        {
            ("bingo", "Choose and confirm a completed Bingo prize."),
            ("numbers", "Privately view your collected Bingo numbers."), ("bingo-status", "Privately view Bingo status.")
        })
            await guild.CreateApplicationCommandAsync(new SlashCommandBuilder().WithName(name).WithDescription(description).Build());

        await guild.CreateApplicationCommandAsync(BuildAdminCommand());
    }

    /// <summary>Builds the administrative command contract without a Discord connection.</summary>
    public static SlashCommandProperties BuildAdminCommand()
    {
        var create = Subcommand("create", "Create a multi-period Bingo event.");
        foreach (var combination in BingoRules.Lines.Keys.Order())
            create.AddOption(new SlashCommandOptionBuilder().WithName("prize-" + BingoRules.Label(combination))
                .WithDescription("Prize for line " + BingoRules.Label(combination)).WithType(ApplicationCommandOptionType.String).WithRequired(true).WithMaxLength(200));
        create.AddOption(new SlashCommandOptionBuilder().WithName("image").WithDescription("Instruction image, up to 8 MiB.")
            .WithType(ApplicationCommandOptionType.Attachment).WithRequired(true));
        create.AddOption(new SlashCommandOptionBuilder().WithName("name").WithDescription("Optional event name.")
            .WithType(ApplicationCommandOptionType.String).WithMaxLength(80));
        create.AddOption(new SlashCommandOptionBuilder().WithName("duration-hours").WithDescription("Default window duration; 24 hours if omitted.")
            .WithType(ApplicationCommandOptionType.Number).WithMinValue(1.0 / 60).WithMaxValue(720));
        create.AddOption(new SlashCommandOptionBuilder().WithName("timezone").WithDescription("Default America/New_York.")
            .WithType(ApplicationCommandOptionType.String));
        create.AddOption(new SlashCommandOptionBuilder().WithName("channel").WithDescription("Game channel; defaults to /setchannel.")
            .WithType(ApplicationCommandOptionType.Channel).AddChannelType(ChannelType.Text));
        var admin = new SlashCommandBuilder().WithName("bingo-admin").WithDescription("Manage Bingo (configured bot administrators only).")
            .WithDefaultMemberPermissions((GuildPermission)0)
            .AddOption(create)
            .AddOption(Subcommand("start-period", "Manually start a period now without publishing instructions."))
            .AddOption(Subcommand("open-next-period", "Prepare an automatic successor with its announcement in a modal."))
            .AddOption(Subcommand("close-period", "Close the current window and start its queued successor, if any."))
            .AddOption(Subcommand("status", "Show the staff audit report privately."))
            .AddOption(Subcommand("end-event", "End the event and retain its audit history."));
        return admin.Build();
    }

    private static SlashCommandOptionBuilder Subcommand(string name, string description) =>
        new SlashCommandOptionBuilder().WithName(name).WithDescription(description).WithType(ApplicationCommandOptionType.SubCommand);

    /// <summary>Routes commands with all player feedback ephemeral; public numbers use the durable publisher.</summary>
    public async Task HandleAsync(SocketSlashCommand command)
    {
        if (!Handles(command.Data.Name)) return;
        await SafeAsync(command, async () =>
        {
            if (command.GuildId != botState.GuildId) throw new BingoException("Use this command in the configured server.");
            if (command.Data.Name == "bingo-admin")
            {
                if (!BotAuthorization.IsAdmin(command.User as SocketGuildUser)) throw new BingoException("You do not have permission to use this command.");
                await HandleAdminAsync(command);
                return;
            }
            await command.DeferAsync(ephemeral: true);
            await service.RecoverAsync();
            var state = await service.ReadAsync() ?? throw new BingoException("No Bingo event exists.");
            switch (command.Data.Name)
            {
                case "bingo":
                    var (flow, options) = await flows.OpenAsync(state.EventId, command.User.Id);
                    if (options.Count == 1)
                        await command.FollowupAsync(ConfirmationText(options[0]), ephemeral: true, components: Confirmation(flow), allowedMentions: AllowedMentions.None);
                    else
                    {
                        var menu = new SelectMenuBuilder().WithCustomId($"bingo:{flow.FlowId:N}:select").WithPlaceholder("Choose one prize")
                            .WithMinValues(1).WithMaxValues(1);
                        foreach (var prize in options)
                            menu.AddOption($"{BingoRules.Label(prize.Combination)} — {Truncate(prize.Description, 85)}", ((int)prize.Combination).ToString());
                        await command.FollowupAsync("BINGO! Choose one prize, then confirm. Prizes are not reserved until confirmation.", ephemeral: true,
                            components: new ComponentBuilder().WithSelectMenu(menu).Build(), allowedMentions: AllowedMentions.None);
                    }
                    break;
                case "numbers":
                    var player = state.Players.GetValueOrDefault(command.User.Id);
                    await ReplyAsync(command, player is null ? "You have not rolled in this event." :
                        $"Your numbers: {string.Join(", ", player.UniqueNumbers.Order())}\nPeriods played: {player.Rolls.Count}; duplicate attempts: {player.Rolls.Count - player.UniqueNumbers.Count}.");
                    break;
                case "bingo-status":
                    await ReplyPagesAsync(command, BingoText.Status(state, command.User.Id));
                    break;
            }
        });
    }

    private async Task HandleAdminAsync(SocketSlashCommand command)
    {
        var sub = command.Data.Options.Single();
        object? Value(string name) => sub.Options.FirstOrDefault(o => o.Name == name)?.Value;
        if (sub.Name == "open-next-period")
        {
            var current = await service.ReadAsync() ?? throw new BingoException("No Bingo event exists.");
            var previousPeriodId = await service.GetSchedulingPeriodIdAsync(current.EventId);
            foreach (var stale in _modals.Where(p => p.Value.ExpiresAtUtc <= clock.GetUtcNow())) _modals.TryRemove(stale.Key, out _);
            if (_modals.Count >= 512) throw new BingoException("Too many open announcement modals. Try again shortly.");
            var id = Guid.NewGuid();
            _modals[id] = new ModalContext(current.EventId, command.User.Id, clock.GetUtcNow().AddMinutes(10), previousPeriodId);
            await command.RespondWithModalAsync(new ModalBuilder().WithTitle("Schedule next Bingo period").WithCustomId($"bingo-open:{id:N}")
                .AddTextInput("Announcement", "announcement", TextInputStyle.Paragraph, maxLength: 4000, required: true).Build());
            return;
        }
        await command.DeferAsync(ephemeral: true);
        if (sub.Name == "create")
        {
            await activityGate.RunAsync(async () =>
            {
                if (Storage.LoadRound().IsActive) throw new BingoException("End the Standard/BossRaid round before creating Bingo.");
                var previous = await service.ReadAsync();
                if (previous?.Status == BingoEventStatus.Active) throw new BingoException("A Bingo event is already active.");
                var channelId = Value("channel") is IChannel selected ? selected.Id : botState.BotChannelId;
                if (botState.Client.GetChannel(channelId) is not IMessageChannel) throw new BingoException("Configure an accessible game channel first using /setchannel.");
                if (botState.BotInfoChannelId == channelId) throw new BingoException("The staff report channel must differ from the public game channel.");
                if (botState.BotInfoChannelId != 0 && botState.Client.GetChannel(botState.BotInfoChannelId) is not IMessageChannel)
                    throw new BingoException("The configured staff channel is missing. Restore or configure it before creating Bingo.");
                var attachment = Value("image") as IAttachment ?? throw new BingoException("An instruction image attachment is required.");
                var name = await images.SaveAsync(attachment.Url, attachment.Filename, attachment.ContentType, attachment.Size);
                var prizes = BingoRules.Lines.Keys.ToDictionary(k => k, k => (string)Value("prize-" + BingoRules.Label(k))!);
                await service.CreateAsync(new BingoCreation((string?)Value("name") ?? "Bingo", channelId, botState.BotInfoChannelId, name, prizes,
                    TimeSpan.FromHours(Value("duration-hours") is { } duration ? Convert.ToDouble(duration) : 24), (string?)Value("timezone") ?? "America/New_York"));
                await ReplyAsync(command, "Bingo event created. Start the first period with /bingo-admin start-period, then use /bingo-admin open-next-period while it is in progress." +
                    (previous?.Deliveries.Any(d => !d.Completed) == true
                        ? " Previous event has undelivered announcements retained in its archived JSON; these will not be retried for the new event."
                        : ""));
            });
        }
        else
        {
            if (sub.Name is "start-period" or "status") await service.RecoverAsync();
            var state = await service.ReadAsync() ?? throw new BingoException("No Bingo event exists.");
            switch (sub.Name)
            {
                case "start-period":
                    await service.StartPeriodAsync(state.EventId);
                    await ReplyAsync(command, "Manual period opened. No instructions were published; post your announcement manually.");
                    break;
                case "close-period":
                    var period = state.Periods.LastOrDefault() ?? throw new BingoException("No period exists.");
                    await service.ClosePeriodAsync(state.EventId, period.PeriodId);
                    await ReplyAsync(command, "Rolling period closed; its scheduled successor, if any, starts automatically. Accumulated numbers and prizes retained.");
                    break;
                case "status":
                    foreach (var page in BingoText.Report(state)) await ReplyAsync(command, page);
                    break;
                case "end-event":
                    await service.EndAsync(state.EventId);
                    await ReplyAsync(command, "Event ended; final data retained for audit.");
                    break;
                default:
                    throw new BingoException("Unknown command. Use /bingo-admin start-period or /bingo-admin open-next-period.");
            }
        }
        await RefreshSafelyAsync();
    }

    /// <summary>Consumes the long-form automatic announcement modal, bound to its administrator and event.</summary>
    public async Task HandleModalAsync(SocketModal modal)
    {
        if (!modal.Data.CustomId.StartsWith("bingo-open:", StringComparison.Ordinal)) return;
        await SafeAsync(modal, async () =>
        {
            await modal.DeferAsync(ephemeral: true);
            if (modal.GuildId != botState.GuildId || !BotAuthorization.IsAdmin(modal.User as SocketGuildUser) ||
                !Guid.TryParseExact(modal.Data.CustomId[11..], "N", out var id) || !_modals.TryGetValue(id, out var context) ||
                context.UserId != modal.User.Id || context.ExpiresAtUtc <= clock.GetUtcNow())
                throw new BingoException("This announcement modal is expired or unauthorized. Open it again.");
            var text = modal.Data.Components.Single(c => c.CustomId == "announcement").Value;
            await service.ScheduleNextPeriodAsync(context.EventId, context.PreviousPeriodId, text);
            _modals.TryRemove(id, out _);
            await ReplyAsync(modal, "Next period scheduled. It will start automatically when the current one closes, using this announcement and the saved image.");
            await RefreshSafelyAsync();
        });
    }

    /// <summary>Validates ownership and server-held flow context before interpreting selects or confirmations.</summary>
    public async Task HandleComponentAsync(SocketMessageComponent component)
    {
        if (!component.Data.CustomId.StartsWith("bingo:", StringComparison.Ordinal)) return;
        await SafeAsync(component, async () =>
        {
            await component.DeferAsync();
            if (component.GuildId != botState.GuildId) throw new BingoException("Use Bingo in the configured server.");
            var parts = component.Data.CustomId.Split(':');
            if (parts.Length != 3 || !Guid.TryParseExact(parts[1], "N", out var flowId)) throw new BingoException("Invalid or expired component.");
            var state = await service.ReadAsync() ?? throw new BingoException("No Bingo event exists.");
            flows.Get(flowId, component.User.Id, state.EventId);
            switch (parts[2])
            {
                case "select":
                    if (component.Data.Values.Count != 1 || !int.TryParse(component.Data.Values.Single(), out var number) ||
                        !Enum.IsDefined((BingoCombination)number)) throw new BingoException("Invalid prize selection.");
                    var (flow, prize) = await flows.ChooseAsync(flowId, component.User.Id, state.EventId, (BingoCombination)number);
                    await component.ModifyOriginalResponseAsync(p => { p.Content = ConfirmationText(prize); p.Components = Confirmation(flow); p.AllowedMentions = AllowedMentions.None; });
                    break;
                case "confirm":
                    var claim = await flows.ConfirmAsync(flowId, component.User.Id, state.EventId);
                    await component.ModifyOriginalResponseAsync(p =>
                    {
                        p.Content = $"BINGO confirmed! You won **{claim.PrizeDescription}**. You are now inactive for this event.";
                        p.Components = new ComponentBuilder().Build(); p.AllowedMentions = AllowedMentions.None;
                    });
                    await RefreshSafelyAsync();
                    break;
                case "cancel":
                    flows.Cancel(flowId, component.User.Id, state.EventId);
                    await component.ModifyOriginalResponseAsync(p =>
                    {
                        p.Content = "Selection cancelled. If you still have an available line, use /bingo before rolling again.";
                        p.Components = new ComponentBuilder().Build();
                    });
                    break;
                default: throw new BingoException("Unknown or expired component.");
            }
        });
    }

    private static MessageComponent Confirmation(BingoClaimFlow flow) => new ComponentBuilder()
        .WithButton("Confirm", $"bingo:{flow.FlowId:N}:confirm", ButtonStyle.Success)
        .WithButton("Cancel", $"bingo:{flow.FlowId:N}:cancel", ButtonStyle.Secondary).Build();
    private static string ConfirmationText(BingoPrize prize) => $"BINGO! Claim **{prize.Description}** for line **{BingoRules.Label(prize.Combination)}**?\nConfirm to leave the event as a winner. This prize is not reserved until confirmation.";
    private static string Truncate(string value, int maximum) => value.Length <= maximum ? value : value[..(char.IsHighSurrogate(value[maximum - 1]) ? maximum - 1 : maximum)] + "…";
    private static Task ReplyAsync(SocketInteraction interaction, string text) =>
        interaction.FollowupAsync(text, ephemeral: true, allowedMentions: AllowedMentions.None);
    private static async Task ReplyPagesAsync(SocketInteraction interaction, string text)
    {
        foreach (var page in BingoText.Pages(text)) await ReplyAsync(interaction, page);
    }

    private async Task RefreshSafelyAsync()
    {
        try { await announcements.RefreshAsync(); }
        catch (Exception ex) { Console.Error.WriteLine($"Bingo presentation failed ({ex.GetType().Name}); state remains committed and recovery will retry."); }
    }

    private static async Task SafeAsync(SocketInteraction interaction, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            var text = ex is BingoException ? ex.Message : "Bingo is temporarily unavailable. State has been preserved; ask an administrator to inspect storage/channel permissions.";
            if (ex is not BingoException) Console.Error.WriteLine($"Bingo interaction failed ({ex.GetType().Name}); no sensitive payload logged.");
            try
            {
                if (interaction.HasResponded) await ReplyAsync(interaction, text);
                else await interaction.RespondAsync(text, ephemeral: true, allowedMentions: AllowedMentions.None);
            }
            catch (Exception responseError)
            {
                Console.Error.WriteLine($"Bingo interaction response expired or failed ({responseError.GetType().Name}); state remains authoritative.");
            }
        }
    }
}
