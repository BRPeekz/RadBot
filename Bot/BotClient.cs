using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using RadBot.Data;
using RadBot.Services;
using DotNetEnv;
using RadBot.Bingo;

namespace RadBot.Bot;

public class BotClient
{
    private readonly IServiceProvider _services;
    private readonly DiscordSocketClient _client;

    public BotClient()
    {
        var config = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds |
                             GatewayIntents.GuildMessages |
                             GatewayIntents.MessageContent |
                             GatewayIntents.GuildMembers
        };

        var services = new ServiceCollection();

        _client = new DiscordSocketClient(config);

        services.AddSingleton(_client);
        var infrastructure = Storage.LoadInfrastructure();
        services.AddSingleton(sp => new BotState
        {
            Client = _client,
            BotChannelId = infrastructure.BotChannelId,
            BotInfoChannelId = infrastructure.BotInfoChannelId
        });

        services.AddSingleton<CommandsHandler>();
        services.AddSingleton<RollInteractionHandler>();
        services.AddSingleton<LotteryService>();
        services.AddSingleton<InfrastructureService>();
        services.AddSingleton<VerificationService>();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<BingoOptions>();
        services.AddSingleton<IBingoRandom, BingoRandom>();
        services.AddSingleton<IBingoRepository, JsonBingoRepository>();
        services.AddSingleton(_ => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30)
        });
        services.AddSingleton<BingoImageStore>();
        services.AddSingleton<BingoService>();
        services.AddSingleton<BingoClaimFlows>();
        services.AddSingleton<IBingoPublisher, DiscordBingoPublisher>();
        services.AddSingleton<BingoAnnouncementService>();
        services.AddSingleton<BingoScheduler>();
        services.AddSingleton<BingoInteractionHandler>();
        services.AddSingleton<LotteryActivityGate>();

        _services = services.BuildServiceProvider();
    }

    public async Task RunAsync()
    {
        var commandHandler = _services.GetRequiredService<CommandsHandler>();
        var verificationService = _services.GetRequiredService<VerificationService>();
        var bingoHandler = _services.GetRequiredService<BingoInteractionHandler>();
        var bingoScheduler = _services.GetRequiredService<BingoScheduler>();

        _client.Log += msg =>
        {
            Console.WriteLine(msg);
            return Task.CompletedTask;
        };
        bool commandsRegistered = false;

        _client.Ready += async () =>
        {
            await bingoScheduler.StartAsync();
            if (!commandsRegistered)
            {
                try
                {
                    await commandHandler.RegisterCommandsAsync();
                    commandsRegistered = true;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Command registration failed ({ex.GetType().Name}); Bingo recovery remains active.");
                }
            }
        };
        _client.MessageReceived += message =>
        {
            verificationService.TrackVerificationAttempt(message);
            return Task.CompletedTask;
        };
        _client.SlashCommandExecuted += commandHandler.HandleSlashCommandAsync;
        _client.SelectMenuExecuted += bingoHandler.HandleComponentAsync;
        _client.ButtonExecuted += bingoHandler.HandleComponentAsync;
        _client.ModalSubmitted += bingoHandler.HandleModalAsync;
        _client.GuildMemberUpdated += async (before, after) =>
        {
            var beforeUser = await before.GetOrDownloadAsync();
            if (beforeUser != null)
                await commandHandler.HandleMemberUpdatedAsync(beforeUser, after);
        };

        var envPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".env");
        Env.Load(envPath);

        string? token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("DISCORD_BOT_TOKEN environment variable is not set.");

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        var state = Storage.LoadRound();

        if (state.IsActive && state.EndTimeUtc is not null)
        {
            var lotteryService = _services.GetRequiredService<LotteryService>();
            lotteryService.ScheduleEndRound(state.EndTimeUtc);
        }

        await Task.Delay(-1);
    }
}

