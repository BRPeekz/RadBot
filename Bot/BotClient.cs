using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using RadBot.Data;
using RadBot.Services;

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
                             GatewayIntents.MessageContent
        };

        var services = new ServiceCollection();

        _client = new DiscordSocketClient(config);

        services.AddSingleton(_client);
        services.AddSingleton(sp => new BotState
        {
            Client = _client,
            BotChannelId = Storage.LoadInfrastructure().BotChannelId
        });

        services.AddSingleton<CommandsHandler>();
        services.AddSingleton<RoundService>();
        services.AddSingleton<InfrastructureService>();

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

        string? token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("DISCORD_BOT_TOKEN environment variable is not set.");

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        var state = Storage.LoadRound();

        if (state.IsActive && state.EndTimeUtc is not null)
        {
            var roundService = _services.GetRequiredService<RoundService>();
            roundService.ScheduleEndRound(state.EndTimeUtc);
        }

        await Task.Delay(-1);
    }
}

