namespace GameServer.Modules.PlayerBots;

/// <summary>Random character names, Russian and English, in the server's rules (3 to 23 letters, digits, '_' or '-').</summary>
static class BotNames
{
    static readonly string[] RuFirst =
    [
        "Ратибор", "Святогор", "Добрыня", "Ярослав", "Всеволод", "Мирослава", "Велемир", "Любава", "Горазд", "Радомир",
        "Злата", "Богдана", "Тихомир", "Вольга", "Светозар", "Милена", "Ждан", "Огнеслав", "Лада", "Берендей",
        "Касьян", "Забава", "Остромир", "Ведана", "Микула", "Снежана", "Гордей", "Василиса", "Будимир", "Ясна",
    ];
    static readonly string[] RuSecond =
    [
        "Грозный", "Тёмный", "Ловкий", "Седой", "Хмурый", "Быстрый", "Каменный", "Северный", "Огненный", "Волчий",
        "Тихий", "Рыжий", "Смелый", "Лесной", "Ночной",
    ];
    static readonly string[] EnFirst =
    [
        "Aldric", "Brynn", "Cassia", "Dorian", "Elara", "Fenwick", "Garrick", "Helka", "Isolde", "Jorund",
        "Kael", "Lyra", "Magnus", "Nyx", "Orrin", "Perrin", "Quill", "Rowena", "Sigrid", "Thane",
        "Ulric", "Vesper", "Wren", "Xander", "Ysolde", "Zarek", "Morrigan", "Corvin", "Saoirse", "Tamsin",
    ];
    static readonly string[] EnSecond =
    [
        "Ashfall", "Blackthorn", "Stormborn", "Grimm", "Duskwalker", "Ironhide", "Frost", "Ravencall", "Emberheart",
        "Nightshade", "Wolfsbane", "Stoneveil", "Brightblade", "Hollow", "Thornwick",
    ];

    /// <summary>A name that <paramref name="taken"/> does not know yet.</summary>
    public static string Pick(Random random, Func<string, bool> taken)
    {
        for (int attempt = 0; ; attempt++)
        {
            bool russian = random.Next(2) == 0;
            var (first, second) = russian ? (RuFirst, RuSecond) : (EnFirst, EnSecond);
            string name = first[random.Next(first.Length)];
            switch (random.Next(4))
            {
                case 0: name += (russian ? "_" : "") + second[random.Next(second.Length)]; break;
                case 1: name += random.Next(2, 999); break;
                case 2: name = second[random.Next(second.Length)] + "_" + name; break;
            }
            if (attempt > 20) name += random.Next(10, 99);
            if (name.Length is >= 3 and <= 23 && !taken(name)) return name;
        }
    }
}
