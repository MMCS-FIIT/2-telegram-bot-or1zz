using System.Text.Json;
using System.Net.Http;
using System.Net;
using System.IO;

namespace SimpleTGBot;

using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

public class TelegramBot
{
    // Токен TG-бота. Можно получить у @BotFather
    private const string BotToken = "ВАШ_ТОКЕН_ИДЕНТИФИКАЦИИ_БОТА";

    // Файлы для хранения данных трекера привычек
    private const string HabitsFilePath = "habits.json";

    // Данные трекера привычек
    private Dictionary<long, List<Habit>> _userHabits = new();
    private Dictionary<long, UserState> _userStates = new();

    /// <summary>
    /// Инициализирует и обеспечивает работу бота до нажатия клавиши Esc
    /// </summary>
    public async Task Run()
    {
        LoadData();

        /*// --- НАСТРОЙКА ПРОКСИ (если нужен) ---
        // Раскомментируйте если используете VPN
        string proxyAddress = "http://127.0.0.1:10809";
        var handler = new HttpClientHandler { Proxy = new WebProxy(proxyAddress), UseProxy = true };
        var httpClient = new HttpClient(handler);
        var botClient = new TelegramBotClient(BotToken, httpClient);*/

        // Стандартное создание клиента (без прокси)
        var botClient = new TelegramBotClient(BotToken);

        using CancellationTokenSource cts = new CancellationTokenSource();

        ReceiverOptions receiverOptions = new ReceiverOptions()
        {
            AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery }
        };

        botClient.StartReceiving(
            updateHandler: OnMessageReceived,
            pollingErrorHandler: OnErrorOccured,
            receiverOptions: receiverOptions,
            cancellationToken: cts.Token
        );

        var me = await botClient.GetMeAsync(cancellationToken: cts.Token);
        Console.WriteLine($"Бот @{me.Username} запущен.\nДля остановки нажмите клавишу Esc...");

        while (Console.ReadKey().Key != ConsoleKey.Escape) { }

        SaveData();
        cts.Cancel();
    }

    /// <summary>
    /// Обработчик события получения сообщения.
    /// </summary>
    async Task OnMessageReceived(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
    {

        if (update.CallbackQuery is not null)
        {
            await HandleCallbackQuery(botClient, update.CallbackQuery, cancellationToken);
            return;
        }

        var message = update.Message;
        if (message is null) return;

        var chatId = message.Chat.Id;

        // --- ОБРАБОТКА ФОТО ---
        if (message.Photo != null && message.Photo.Length > 0)
        {
            await HandlePhotoAsync(botClient, message, cancellationToken);
            return;
        }

        // --- ОБРАБОТКА СТИКЕРОВ ---
        if (message.Sticker != null)
        {
            await HandleStickerAsync(botClient, message, cancellationToken);
            return;
        }


        if (message.Text is not { } messageText)
        {
            await botClient.SendTextMessageAsync(chatId, "Я понимаю только текст, фото и стикеры! 😊", cancellationToken: cancellationToken);
            return;
        }

        Console.WriteLine($"Получено сообщение в чате {chatId}: '{messageText}'");


        if (_userStates.TryGetValue(chatId, out var state))
        {
            await HandleDialogState(botClient, chatId, messageText, state, cancellationToken);
            return;
        }


        if (messageText.StartsWith("/"))
        {
            await HandleCommand(botClient, chatId, messageText, cancellationToken);
            return;
        }

        await botClient.SendTextMessageAsync(
            chatId: chatId,
            text: "Ты написал:\n" + messageText,
            cancellationToken: cancellationToken);
    }

    // ========== ДОПОЛНИТЕЛЬНЫЕ МЕТОДЫ ==========

    /// <summary>
    /// Получение случайного факта о кошках из API
    /// </summary>
    private async Task<string> GetRandomCatFactAsync()
    {
        string proxyAddress = null; // Замените null на адрес вашего прокси, если он нужен
        try
        {
            HttpClientHandler handler = new HttpClientHandler();
            if (!string.IsNullOrEmpty(proxyAddress))
            {
                handler.Proxy = new WebProxy(proxyAddress);
                handler.UseProxy = true;
                Console.WriteLine($"API-запрос идёт через прокси: {proxyAddress}");
            }

            using var httpClient = new HttpClient(handler);
            httpClient.Timeout = TimeSpan.FromSeconds(10);


            var url = "https://catfact.ninja/fact";

            var response = await httpClient.GetStringAsync(url);
            var factData = JsonSerializer.Deserialize<JsonElement>(response);
            var catFact = factData.GetProperty("fact").GetString();

            return $"🐱 {catFact}";
        }
        catch
        {

            var facts = new[]
            {
                "🐱 Кошки спят 12-16 часов в сутки!",
                "🐱 У кошек 32 мышцы в каждом ухе!",
                "🐱 Кошки не чувствуют сладкий вкус!",
                "🐱 Сердце кошки бьётся вдвое быстрее человеческого!",
                "🐱 Кошки могут издавать около 100 разных звуков!",
                "🐱 Усы помогают кошкам определять, пролезут ли они в отверстие!",
                "🐱 Кошки видят в темноте в 6 раз лучше людей!"
            };

            var random = new Random();
            return $"🐱 *Котофакт:*\n\n{facts[random.Next(facts.Length)]}\n\n🐾 Мур-мур!";
        }
    }

    /// <summary>
    /// Обработка полученного фото
    /// </summary>
    private async Task HandlePhotoAsync(ITelegramBotClient botClient, Message message, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var userName = message.From?.FirstName ?? "Пользователь";

        Console.WriteLine($"Получено фото от {userName}");

        await botClient.SendTextMessageAsync(
            chatId: chatId,
            text: $"📸 {userName}, отличное фото! Хотите узнать интересный факт о кошках? Отправьте /catfact",
            cancellationToken: ct);
    }

    /// <summary>
    /// Обработка стикеров
    /// </summary>
    private async Task HandleStickerAsync(ITelegramBotClient botClient, Message message, CancellationToken ct)
    {
        var chatId = message.Chat.Id;
        var emoji = message.Sticker?.Emoji ?? "😊";
        var userName = message.From?.FirstName ?? "Пользователь";

        Console.WriteLine($"Получен стикер от {userName}, эмодзи: {emoji}");

        await botClient.SendTextMessageAsync(
            chatId: chatId,
            text: $"🎨 {userName}, классный стикер с эмоцией {emoji}! 🎉\nХотите узнать факт о кошках? Отправьте /catfact",
            cancellationToken: ct);
    }

    private async Task HandleCommand(ITelegramBotClient botClient, long chatId, string command, CancellationToken ct)
    {
        switch (command.ToLower())
        {
            case "/start":
                await ShowWelcomeMessage(botClient, chatId, ct);
                break;
            case "/menu":
                await ShowMainMenu(botClient, chatId, ct);
                break;
            case "/habits":
                await ShowHabitsList(botClient, chatId, ct);
                break;
            case "/add":
                _userStates[chatId] = new UserState { Action = "waiting_habit_name" };
                await botClient.SendTextMessageAsync(chatId, "📝 Введите название новой привычки:", cancellationToken: ct);
                break;
            case "/stats":
                await ShowStatistics(botClient, chatId, ct);
                break;
            case "/today":
                await ShowTodayHabits(botClient, chatId, ct);
                break;
            case "/catfact":
            case "/fact":
                var fact = await GetRandomCatFactAsync();

                var factKeyboard = new InlineKeyboardMarkup(new[]
                {
                    new[] { InlineKeyboardButton.WithCallbackData("🐱 Ещё факт", "random_cat_fact") },
                    new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "main_menu") }
                });

                await botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: fact,
                    parseMode: ParseMode.Markdown,
                    replyMarkup: factKeyboard,
                    cancellationToken: ct);
                break;
            default:
                await botClient.SendTextMessageAsync(chatId, "❓ Неизвестная команда. Используйте /menu", cancellationToken: ct);
                break;
        }
    }

    private async Task HandleDialogState(ITelegramBotClient botClient, long chatId, string text, UserState state, CancellationToken ct)
    {
        switch (state.Action)
        {
            case "waiting_habit_name":
                await AddNewHabit(botClient, chatId, text, ct);
                break;
            case "waiting_habit_description":
                await AddHabitDescription(botClient, chatId, state.TempHabitId, text, ct);
                break;
            case "waiting_habit_goal":
                await SetHabitGoal(botClient, chatId, state.TempHabitId, text, ct);
                break;
            default:
                _userStates.Remove(chatId);
                await ShowMainMenu(botClient, chatId, ct);
                break;
        }
    }

    private async Task HandleCallbackQuery(ITelegramBotClient botClient, CallbackQuery callbackQuery, CancellationToken ct)
    {
        var chatId = callbackQuery.Message.Chat.Id;
        var data = callbackQuery.Data;

        await botClient.AnswerCallbackQueryAsync(callbackQuery.Id, cancellationToken: ct);

        if (data == null) return;

        if (data == "my_habits")
        {
            await ShowHabitsList(botClient, chatId, ct);
        }
        else if (data == "add_habit")
        {
            _userStates[chatId] = new UserState { Action = "waiting_habit_name" };
            await botClient.SendTextMessageAsync(chatId, "📝 Введите название новой привычки:", cancellationToken: ct);
        }
        else if (data == "mark_today")
        {
            await ShowTodayHabits(botClient, chatId, ct);
        }
        else if (data == "show_stats")
        {
            await ShowStatistics(botClient, chatId, ct);
        }
        else if (data == "random_cat_fact")
        {
            var fact = await GetRandomCatFactAsync();

            var factKeyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("🐱 Ещё факт", "random_cat_fact") },
                new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "main_menu") }
            });

            await botClient.SendTextMessageAsync(
                chatId: chatId,
                text: fact,
                parseMode: ParseMode.Markdown,
                replyMarkup: factKeyboard,
                cancellationToken: ct);
        }
        else if (data.StartsWith("check_habit_"))
        {
            var habitId = int.Parse(data.Replace("check_habit_", ""));
            await MarkHabitDone(botClient, chatId, habitId, ct);
            await ShowTodayHabits(botClient, chatId, ct);
        }
        else if (data.StartsWith("delete_habit_"))
        {
            var habitId = int.Parse(data.Replace("delete_habit_", ""));
            await DeleteHabit(botClient, chatId, habitId, ct);
            await ShowHabitsList(botClient, chatId, ct);
        }
        else if (data == "main_menu")
        {
            await ShowMainMenu(botClient, chatId, ct);
        }
    }

    private async Task ShowWelcomeMessage(ITelegramBotClient botClient, long chatId, CancellationToken ct)
    {
        if (!_userHabits.ContainsKey(chatId))
        {
            _userHabits[chatId] = new List<Habit>();
            SaveData();
        }

        var welcomeText = @"🍅 *Добро пожаловать в Трекер Привычек!* 🏆

Я помогу тебе следить за ежедневными привычками и достигать целей.

*Что нового:*
📸 Отправьте фото — я его замечу!
🎨 Отправьте стикер — я отвечу!
🐱 Команда /catfact — интересный факт о кошках

*Команды:*
/menu - Главное меню
/habits - Мои привычки
/add - Добавить привычку
/today - Отметить выполнение за сегодня
/stats - Моя статистика
/catfact - Случайный факт о кошках

Начни с добавления первой привычки через /add!";

        await botClient.SendTextMessageAsync(chatId, welcomeText, parseMode: ParseMode.Markdown, cancellationToken: ct);
    }

    private async Task ShowMainMenu(ITelegramBotClient botClient, long chatId, CancellationToken ct)
    {
        var buttons = new List<List<InlineKeyboardButton>>
        {
            new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("📋 Мои привычки", "my_habits") },
            new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("➕ Добавить привычку", "add_habit") },
            new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("✅ Отметить за сегодня", "mark_today") },
            new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("📊 Статистика", "show_stats") },
            new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🐱 Котофакт", "random_cat_fact") }
        };

        var inlineKeyboard = new InlineKeyboardMarkup(buttons);
        await botClient.SendTextMessageAsync(chatId, "🏠 *Главное меню*\nВыберите действие:", parseMode: ParseMode.Markdown, replyMarkup: inlineKeyboard, cancellationToken: ct);
    }

    private async Task AddNewHabit(ITelegramBotClient botClient, long chatId, string habitName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(habitName))
        {
            await botClient.SendTextMessageAsync(chatId, "❌ Название не может быть пустым. Попробуйте ещё раз:", cancellationToken: ct);
            return;
        }

        var habitId = _userHabits[chatId].Count + 1;
        var newHabit = new Habit
        {
            Id = habitId,
            Name = habitName.Trim(),
            CreatedAt = DateTime.Now,
            CompletedDates = new List<DateTime>()
        };

        _userHabits[chatId].Add(newHabit);
        _userStates[chatId] = new UserState { Action = "waiting_habit_description", TempHabitId = newHabit.Id };

        await botClient.SendTextMessageAsync(chatId, $"✅ Привычка \"{habitName}\" создана!\nТеперь добавьте описание (или отправьте /skip чтобы пропустить):", cancellationToken: ct);
        SaveData();
    }

    private async Task AddHabitDescription(ITelegramBotClient botClient, long chatId, int habitId, string description, CancellationToken ct)
    {
        var habit = _userHabits[chatId].FirstOrDefault(h => h.Id == habitId);
        if (habit != null)
        {
            habit.Description = description == "/skip" ? "Без описания" : description;
        }

        _userStates[chatId] = new UserState { Action = "waiting_habit_goal", TempHabitId = habitId };

        await botClient.SendTextMessageAsync(chatId, "🎯 Какова ваша цель? (например: 7 дней в неделю, каждый день, 5 раз в неделю)\nИли отправьте /skip:", cancellationToken: ct);
    }

    private async Task SetHabitGoal(ITelegramBotClient botClient, long chatId, int habitId, string goal, CancellationToken ct)
    {
        var habit = _userHabits[chatId].FirstOrDefault(h => h.Id == habitId);
        if (habit != null)
        {
            habit.Goal = goal == "/skip" ? "Ежедневно" : goal;
        }

        _userStates.Remove(chatId);

        await botClient.SendTextMessageAsync(chatId, $"✨ Привычка полностью настроена!\n\nИспользуйте /today чтобы отметить выполнение!", cancellationToken: ct);
        SaveData();
    }

    private async Task ShowHabitsList(ITelegramBotClient botClient, long chatId, CancellationToken ct)
    {
        if (!_userHabits.ContainsKey(chatId) || _userHabits[chatId].Count == 0)
        {
            await botClient.SendTextMessageAsync(chatId, "📭 У вас пока нет привычек. Используйте /add чтобы добавить первую!", cancellationToken: ct);
            return;
        }

        var habits = _userHabits[chatId];
        var message = "*📋 Ваши привычки:*\n\n";
        var buttons = new List<List<InlineKeyboardButton>>();

        foreach (var habit in habits)
        {
            message += $"*{habit.Id}.* {habit.Name}\n";
            message += $"   📝 {habit.Description}\n";
            message += $"   🎯 Цель: {habit.Goal}\n";
            var completedToday = habit.CompletedDates.Any(d => d.Date == DateTime.Today);
            message += $"   ✅ Сегодня: {(completedToday ? "Выполнено ✓" : "Не выполнено")}\n\n";

            buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData($"🗑 Удалить \"{habit.Name}\"", $"delete_habit_{habit.Id}") });
        }

        buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "main_menu") });

        var inlineKeyboard = new InlineKeyboardMarkup(buttons);
        await botClient.SendTextMessageAsync(chatId, message, parseMode: ParseMode.Markdown, replyMarkup: inlineKeyboard, cancellationToken: ct);
    }

    private async Task ShowTodayHabits(ITelegramBotClient botClient, long chatId, CancellationToken ct)
    {
        if (!_userHabits.ContainsKey(chatId) || _userHabits[chatId].Count == 0)
        {
            await botClient.SendTextMessageAsync(chatId, "📭 У вас нет привычек. Добавьте первую через /add!", cancellationToken: ct);
            return;
        }

        var today = DateTime.Today;
        var habits = _userHabits[chatId];
        var notCompleted = habits.Where(h => !h.CompletedDates.Contains(today)).ToList();

        if (notCompleted.Count == 0)
        {
            await botClient.SendTextMessageAsync(chatId, "🎉 Поздравляю! Все привычки на сегодня выполнены!", cancellationToken: ct);
            return;
        }

        var buttons = new List<List<InlineKeyboardButton>>();
        foreach (var habit in notCompleted)
        {
            buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData($"☐ {habit.Name}", $"check_habit_{habit.Id}") });
        }

        buttons.Add(new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "main_menu") });

        var inlineKeyboard = new InlineKeyboardMarkup(buttons);
        await botClient.SendTextMessageAsync(chatId, "✅ *Отметить выполнение за сегодня:*", parseMode: ParseMode.Markdown, replyMarkup: inlineKeyboard, cancellationToken: ct);
    }

    private async Task MarkHabitDone(ITelegramBotClient botClient, long chatId, int habitId, CancellationToken ct)
    {
        var habit = _userHabits[chatId].FirstOrDefault(h => h.Id == habitId);
        if (habit != null)
        {
            var today = DateTime.Today;
            if (!habit.CompletedDates.Contains(today))
            {
                habit.CompletedDates.Add(today);
                await botClient.SendTextMessageAsync(chatId, $"✅ Привычка \"{habit.Name}\" отмечена как выполненная!", cancellationToken: ct);
                SaveData();
            }
            else
            {
                await botClient.SendTextMessageAsync(chatId, $"⚠️ Привычка \"{habit.Name}\" уже отмечена сегодня.", cancellationToken: ct);
            }
        }
    }

    private async Task DeleteHabit(ITelegramBotClient botClient, long chatId, int habitId, CancellationToken ct)
    {
        var habit = _userHabits[chatId].FirstOrDefault(h => h.Id == habitId);
        if (habit != null)
        {
            _userHabits[chatId].Remove(habit);
            await botClient.SendTextMessageAsync(chatId, $"🗑 Привычка \"{habit.Name}\" удалена.", cancellationToken: ct);
            SaveData();
        }
    }

    private async Task ShowStatistics(ITelegramBotClient botClient, long chatId, CancellationToken ct)
    {
        if (!_userHabits.ContainsKey(chatId) || _userHabits[chatId].Count == 0)
        {
            await botClient.SendTextMessageAsync(chatId, "📭 Нет привычек для статистики.", cancellationToken: ct);
            return;
        }

        var habits = _userHabits[chatId];
        var message = "*📊 Ваша статистика:*\n\n";

        foreach (var habit in habits)
        {
            var totalDays = (DateTime.Now - habit.CreatedAt).Days + 1;
            var completedDays = habit.CompletedDates.Count;
            var successRate = totalDays > 0 ? (completedDays * 100 / totalDays) : 0;

            var streak = 0;
            var currentDate = DateTime.Today;
            while (habit.CompletedDates.Contains(currentDate))
            {
                streak++;
                currentDate = currentDate.AddDays(-1);
            }

            message += $"*{habit.Name}*\n";
            message += $"   ✅ Выполнено: {completedDays} из {totalDays} дней\n";
            message += $"   📈 Успех: {successRate}%\n";
            message += $"   🔥 Серия: {streak} дней\n\n";
        }

        var buttons = new List<List<InlineKeyboardButton>> { new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "main_menu") } };
        var inlineKeyboard = new InlineKeyboardMarkup(buttons);
        await botClient.SendTextMessageAsync(chatId, message, parseMode: ParseMode.Markdown, replyMarkup: inlineKeyboard, cancellationToken: ct);
    }

    private void LoadData()
    {
        try
        {
            if (System.IO.File.Exists(HabitsFilePath))
            {
                var json = System.IO.File.ReadAllText(HabitsFilePath);
                _userHabits = JsonSerializer.Deserialize<Dictionary<long, List<Habit>>>(json) ?? new Dictionary<long, List<Habit>>();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка загрузки данных: {ex.Message}");
            _userHabits = new Dictionary<long, List<Habit>>();
        }
    }

    private void SaveData()
    {
        try
        {
            var json = JsonSerializer.Serialize(_userHabits);
            System.IO.File.WriteAllText(HabitsFilePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка сохранения данных: {ex.Message}");
        }
    }

    Task OnErrorOccured(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
    {
        var errorMessage = exception switch
        {
            ApiRequestException apiRequestException
                => $"Telegram API Error:\n[{apiRequestException.ErrorCode}]\n{apiRequestException.Message}",
            _ => exception.ToString()
        };

        Console.WriteLine(errorMessage);
        return Task.CompletedTask;
    }
}


public class Habit
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Goal { get; set; } = "Ежедневно";
    public DateTime CreatedAt { get; set; }
    public List<DateTime> CompletedDates { get; set; } = new();
}

// Состояние диалога пользователя
public class UserState
{
    public string Action { get; set; } = "";
    public int TempHabitId { get; set; }
}