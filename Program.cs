using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using MongoDB.Driver;
using Quartz;
using Quartz.Impl;

class Program
{
    private static TelegramBotClient? botClient;
    private static IMongoCollection<Promotion>? collection;
    private static long CHAT_ID;

    public class Promotion
    {
        public int Hora { get; set; }
        public string? Url { get; set; }
    }

    static async Task Main()
    {
        DotNetEnv.Env.Load();

        var token = Environment.GetEnvironmentVariable("BOT_TOKEN");
        var mongoUri = Environment.GetEnvironmentVariable("MONGO_URI");
        var chatIdEnv = Environment.GetEnvironmentVariable("CHAT_ID");

        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(mongoUri) || string.IsNullOrEmpty(chatIdEnv))
        {
            Console.WriteLine("Variáveis de ambiente não configuradas.");
            return;
        }

        CHAT_ID = long.Parse(chatIdEnv);

        botClient = new TelegramBotClient(token);

        var client = new MongoClient(mongoUri);
        var db = client.GetDatabase("Telegram_bott");
        collection = db.GetCollection<Promotion>("links");

        Console.WriteLine("Bot conectado e MongoDB ok.");

        using var cts = new CancellationTokenSource();

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = Array.Empty<Telegram.Bot.Types.Enums.UpdateType>()
        };

        botClient.StartReceiving(
            HandleUpdateAsync,
            HandleErrorAsync,
            receiverOptions,
            cts.Token
        );

        var me = await botClient.GetMeAsync();
        Console.WriteLine($"Bot {me.Username} iniciado...");

        await StartScheduler();

        await Task.Delay(Timeout.Infinite); // mantém rodando no Render
    }

    static List<Promotion> LoadPromotions()
    {
        return collection?.Find(Builders<Promotion>.Filter.Empty).ToList() ?? new List<Promotion>();
    }

    static string? GetLink(int hora)
    {
        var promo = LoadPromotions().Find(p => p.Hora == hora);
        return promo?.Url;
    }

    static async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken token)
    {
        if (update.Message is not { } message) return;
        if (message.Text is not { } text) return;

        if (text.StartsWith("/start"))
        {
            await bot.SendTextMessageAsync(message.Chat.Id,
                "Olá, bem-vindo ao Achadinhos Imperdíveis! Envie /help para ver os comandos disponíveis.");
        }
        else if (text.StartsWith("/help"))
        {
            await bot.SendTextMessageAsync(message.Chat.Id,
                "Comandos disponíveis:\n/start\n/help\n/promo\n/contato\n/info\n/feedback");
        }
        else if (text.StartsWith("/promo"))
        {
            var keyboard = new List<List<InlineKeyboardButton>>();

            for (int hora = 9; hora <= 19; hora++)
            {
                var link = GetLink(hora);

                if (!string.IsNullOrEmpty(link))
                {
                    keyboard.Add(new List<InlineKeyboardButton> {
                        InlineKeyboardButton.WithUrl($"Promoção {hora}h", link)
                    });
                }
            }

            var replyMarkup = new InlineKeyboardMarkup(keyboard);

            await bot.SendTextMessageAsync(message.Chat.Id,
                "Escolha uma promoção:", replyMarkup: replyMarkup);
        }
        else if (text.StartsWith("/contato"))
        {
            await bot.SendTextMessageAsync(message.Chat.Id,
                "Para falar com o suporte, envie mensagem para (11)97711-2443 dentro do Telegram.");
        }
        else if (text.StartsWith("/info"))
        {
            await bot.SendTextMessageAsync(message.Chat.Id,
                "Achadinhos Imperdíveis é um canal de promoções da Shopee, atualizado diariamente.");
        }
        else if (text.StartsWith("/feedback"))
        {
            await bot.SendTextMessageAsync(message.Chat.Id,
                "Agradecemos seu feedback! Compartilhe sugestões ou opiniões sobre os produtos divulgados.");
        }
    }

    static Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken token)
    {
        Console.WriteLine(exception.Message);
        return Task.CompletedTask;
    }

    static async Task StartScheduler()
    {
        StdSchedulerFactory factory = new StdSchedulerFactory();
        IScheduler scheduler = await factory.GetScheduler();
        await scheduler.Start();

        for (int hora = 9; hora <= 19; hora++)
        {
            IJobDetail job = JobBuilder.Create<PromoJob>()
                .UsingJobData("hora", hora)
                .Build();

            ITrigger trigger = TriggerBuilder.Create()
                .WithDailyTimeIntervalSchedule(x =>
                    x.StartingDailyAt(TimeOfDay.HourAndMinuteOfDay(hora, 0))
                    .OnEveryDay())
                .Build();

            await scheduler.ScheduleJob(job, trigger);
        }
    }

    public class PromoJob : IJob
    {
        public async Task Execute(IJobExecutionContext context)
        {
            int hora = context.JobDetail.JobDataMap.GetInt("hora");
            string? link = GetLink(hora);

            if (!string.IsNullOrEmpty(link) && botClient != null)
            {
                var keyboard = new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithUrl($"Promoção {hora}h", link) }
                });

                await botClient.SendTextMessageAsync(
                    chatId: CHAT_ID,
                    text: $"Confira a promoção das {hora}h:",
                    replyMarkup: keyboard
                );
            }
        }
    }
}