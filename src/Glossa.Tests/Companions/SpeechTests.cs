using Glossa.Core.Companions;

namespace Glossa.Tests.Companions;

public sealed class SpeechTests : IDisposable
{
    private readonly TestFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private Companion Geralt()
    {
        var root = _folders.New();
        CatalogTests.Character(root, "4-common", "geralt", json: """
            {"name": "Геральт", "game": "The Witcher 3", "gender": "m",
             "phrases": {"greet": ["Хм.", "Ну здравствуй."], "study_due": ["Заказ: {cards}."],
                         "streak_risk": ["Серия в {days} вот-вот сгорит. Зараза."], "click.tired": [], "odd": "not a list"}}
            """);
        return CompanionCatalog.Load([root]).Find("geralt")!;
    }

    [Fact]
    public void The_lines_come_from_the_manifest_with_the_numbers_put_in()
    {
        var g = Geralt();
        Assert.Equal("Заказ: 12 карточек.", CompanionSpeech.Say(g, SpeechEvents.StudyDue, new Random(1), null, cards: 12));
        Assert.Equal("Заказ: 3 карточки.", CompanionSpeech.Say(g, SpeechEvents.StudyDue, new Random(1), null, cards: 3));
        Assert.Equal("Серия в 21 день вот-вот сгорит. Зараза.", CompanionSpeech.Say(g, SpeechEvents.StreakRisk, new Random(1), null, days: 21));
        Assert.Null(CompanionSpeech.Say(g, SpeechEvents.Click(Mood.Tired), new Random(1), null));
        Assert.Null(CompanionSpeech.Say(g, "odd", new Random(1), null));
    }

    [Fact]
    public void A_line_about_the_cards_or_the_series_waits_for_its_number_and_blank_lines_are_never_said()
    {
        var root = _folders.New();
        CatalogTests.Character(root, "4-common", "geralt", json: """
            {"name": "Геральт", "phrases": {"study_due": ["Заказ: {cards}.", "  ", "Есть работа."],
                                            "streak_risk": ["Серия в {days} сгорит."], "greet": ["", " "]}}
            """);
        var g = CompanionCatalog.Load([root]).Find("geralt")!;

        Assert.Equal(["Заказ: {cards}.", "Есть работа."], g.Phrases!["study_due"]);
        for (var seed = 0; seed < 10; seed++)
            Assert.Equal("Есть работа.", CompanionSpeech.Say(g, SpeechEvents.StudyDue, new Random(seed), null, cards: 0));
        Assert.Null(CompanionSpeech.Say(g, SpeechEvents.StreakRisk, new Random(1), null, days: 0));
        Assert.Null(CompanionSpeech.Say(g, SpeechEvents.Greet, new Random(1), null));
    }

    [Fact]
    public void A_number_and_a_short_word_keep_to_the_next_word_and_a_row_never_starts_with_a_dash()
    {
        const char Nb = ' ';
        Assert.Equal($"Заказ: 6{Nb}карточек. Плата{Nb}- память.", Glossa.Core.Text.Russian.NoBreaks("Заказ: 6 карточек. Плата - память."));
        Assert.Equal($"В{Nb}гильдии висят 12{Nb}карточек", Glossa.Core.Text.Russian.NoBreaks("В гильдии висят 12 карточек"));
        Assert.Equal($"Не{Nb}тяни, а{Nb}в{Nb}игру", Glossa.Core.Text.Russian.NoBreaks("Не тяни, а в игру"));
        Assert.Equal("Учёба ждёт.", Glossa.Core.Text.Russian.NoBreaks("Учёба ждёт."));
    }

    [Fact]
    public void A_zone_of_the_manifest_answers_a_click_inside_it_the_first_listed_winning()
    {
        var root = _folders.New();
        CatalogTests.Character(root, "3-rare", "2b", 238, json: """
            {"name": "2B", "zones": {"chest": [[105, 50, 28, 15]], "sword": [[22, 165, 32, 45], [45, 122, 40, 48]],
                                     "broken": [[1, 2]], "wide": [[0, 0, 238, 238]]},
             "phrases": {"poke.sword": ["Мой меч не игрушка."]}}
            """);
        var c = CompanionCatalog.Load([root]).Find("2b")!;

        Assert.Equal(new[] { "chest", "sword", "wide" }, c.Zones!.Select(z => z.Name));
        Assert.Equal("chest", c.ZoneAt(110, 55));
        Assert.Equal("sword", c.ZoneAt(50, 130));
        Assert.Equal("sword", c.ZoneAt(30, 200));
        Assert.Equal("wide", c.ZoneAt(200, 10));
        Assert.Equal("Мой меч не игрушка.", CompanionSpeech.Say(c, SpeechEvents.Poke("sword"), new Random(1), null));
    }

    [Fact]
    public void Four_clicks_within_three_seconds_are_pestering_and_the_count_starts_again()
    {
        var pokes = new CompanionPokes();
        var t = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(pokes.Pester(t));
        Assert.False(pokes.Pester(t.AddSeconds(1)));
        Assert.False(pokes.Pester(t.AddSeconds(5)));
        Assert.False(pokes.Pester(t.AddSeconds(5.5)));
        Assert.False(pokes.Pester(t.AddSeconds(6)));
        Assert.True(pokes.Pester(t.AddSeconds(6.5)));
        Assert.False(pokes.Pester(t.AddSeconds(7)));
    }

    [Fact]
    public void The_same_line_never_comes_twice_in_a_row()
    {
        var g = Geralt();
        var rng = new Random(3);
        string? last = null;
        for (var i = 0; i < 40; i++)
        {
            var line = CompanionSpeech.Say(g, SpeechEvents.Greet, rng, last);
            Assert.NotEqual(last, line);
            last = line;
        }
    }

    [Fact]
    public void Opening_the_home_page_warns_of_the_series_first_then_of_the_cards_else_greets()
    {
        var evening = new DateTime(2026, 10, 3, 19, 0, 0);
        var morning = new DateTime(2026, 10, 3, 9, 0, 0);
        Assert.Equal(SpeechEvents.StreakRisk, CompanionSpeech.OnOpen(evening, todayPoints: 0, streakDays: 5, cardsDue: 8, translate: false));
        Assert.Equal(SpeechEvents.StudyDue, CompanionSpeech.OnOpen(morning, todayPoints: 0, streakDays: 5, cardsDue: 8, translate: false));
        Assert.Equal(SpeechEvents.Greet, CompanionSpeech.OnOpen(morning, 0, 5, cardsDue: 8, translate: true));
        Assert.Equal(SpeechEvents.Greet, CompanionSpeech.OnOpen(evening, todayPoints: 2, streakDays: 5, cardsDue: 0, translate: false));
    }

    [Fact]
    public void A_change_of_mood_is_said_and_waking_up_has_its_own_line()
    {
        Assert.Null(CompanionSpeech.OnMoodChange(null, Mood.Happy));
        Assert.Null(CompanionSpeech.OnMoodChange(Mood.Happy, Mood.Happy));
        Assert.Equal(SpeechEvents.Wake, CompanionSpeech.OnMoodChange(Mood.Asleep, Mood.Calm));
        Assert.Equal("mood.tired", CompanionSpeech.OnMoodChange(Mood.Happy, Mood.Tired));
        Assert.Null(CompanionSpeech.OnMoodChange(Mood.Happy, Mood.Calm));
    }
}
