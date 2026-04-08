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
using System.Net;
using System.Text;

class Program
{
    private static TelegramBotClient? botClient;
    private static IMongoCollection<Promotion>? collection;
    private static long CHAT_ID;

    // CACHE
    private static List<Promotion> _cache = new();

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

        LoadCache();

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

        StartHttpListener();

        await Task.Delay(Timeout.Infinite);
    }

    static void StartHttpListener()
    {
        var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://*:{port}/");
        listener.Start();

        Console.WriteLine($"Listening on port {port}");

        _ = Task.Run(async () =>
        {
            while (true)
            {
                var context = await listener.GetContextAsync();
                var response = context.Response;

                var buffer = Encoding.UTF8.GetBytes("Bot está rodando!");
                response.ContentLength64 = buffer.Length;
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                response.Close();
            }
        });
    }

    static void LoadCache()
    {
        try
        {
            _cache = collection?.Find(_ => true).ToList() ?? new List<Promotion>();
            Console.WriteLine("Cache carregado com sucesso.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("Erro ao carregar cache:");
            Console.WriteLine(ex.ToString());
        }
    }

    static string? GetLink(int hora)
    {
        return _cache.Find(p => p.Hora == hora)?.Url;
    }

    static async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken token)
    {
        try
        {
            if (update.Message is not { } message) return;
            if (message.Text is not { } text) return;

            Console.WriteLine($"Recebido: {text}");

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

                if (keyboard.Count == 0)
                {
                    await bot.SendTextMessageAsync(message.Chat.Id, "Nenhuma promoção disponível no momento.");
                    return;
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
        catch (Exception ex)
        {
            Console.WriteLine("ERRO NO UPDATE:");
            Console.WriteLine(ex.ToString());
        }
    }

    static Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken token)
    {
        Console.WriteLine("ERRO GLOBAL:");
        Console.WriteLine(exception.ToString());
        return Task.CompletedTask;
    }

    static async Task StartScheduler()
    {
        StdSchedulerFactory factory = new StdSchedulerFactory();
        IScheduler scheduler = await factory.GetScheduler();
        await scheduler.Start();

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time");

        for (int hora = 9; hora <= 19; hora++)
        {
            IJobDetail job = JobBuilder.Create<PromoJob>()
                .UsingJobData("hora", hora)
                .Build();

            ITrigger trigger = TriggerBuilder.Create()
                .WithDailyTimeIntervalSchedule(x =>
                    x
                    .StartingDailyAt(TimeOfDay.HourAndMinuteOfDay(hora, 0))
                    .InTimeZone(timeZone)
                    .OnEveryDay())
                .Build();

            await scheduler.ScheduleJob(job, trigger);
        }
    }

    public class PromoJob : IJob
    {
        public async Task Execute(IJobExecutionContext context)
        {
            try
            {
                int hora = context.JobDetail.JobDataMap.GetInt("hora");
                string? link = GetLink(hora);

                Console.WriteLine($"Executando job: {hora}h - {DateTime.Now}");

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
            catch (Exception ex)
            {
                Console.WriteLine("ERRO NO JOB:");
                Console.WriteLine(ex.ToString());
            }
        }
    }
}