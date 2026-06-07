using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using RadBot.Data;
using RadBot.Services;
using DotNetEnv;

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
        services.AddSingleton<LotteryService>();
        services.AddSingleton<InfrastructureService>();
        services.AddSingleton<VerificationService>();

        _services = services.BuildServiceProvider();
    }

    public async Task RunAsync()
    {
        var commandHandler = _services.GetRequiredService<CommandsHandler>();

        _client.Log += msg =>
        {
            Console.WriteLine(msg);
            return Task.CompletedTask;
        };
        bool commandsRegistered = false;

        _client.Ready += async () =>
        {
            if (!commandsRegistered)
            {
                await commandHandler.RegisterCommandsAsync();
                commandsRegistered = true;
            }
        };
        _client.MessageReceived += commandHandler.HandleCommandAsync;
        _client.SlashCommandExecuted += commandHandler.HandleSlashCommandAsync;
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

