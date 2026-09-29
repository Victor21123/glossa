using Glossa.Core.Library;

namespace Glossa.Tests.Library;

public class KnownWordsTests
{
    private static SavedWord Word(string lang, string word, string? lemma = null, bool pinned = false, params string[] surfaces) => new()
    {
        Language = lang, Word = word, DictionaryForm = lemma, Pinned = pinned,
        Contexts = surfaces.Select(s => new WordContext { Surface = s }).ToList(),
    };

    [Fact]
    public void Finds_a_saved_word_by_itself_its_dictionary_form_or_a_form_it_was_met_in()
    {
        var known = new KnownWords([
            Word("en", "reconsider"),
            Word("ja", "合体して", "合体する", surfaces: "合体して"),
            Word("en", "shit-stained", pinned: true),
            Word("en", "blew", "blow", surfaces: "blew"),
        ]);

        Assert.False(known.Find("Reconsider"));   // any case
        Assert.False(known.Find("合体する"));
        Assert.False(known.Find("blow"));
        Assert.True(known.Find("shit-stained"));  // «Не могу запомнить»
        Assert.Null(known.Find("position"));
    }

    [Fact]
    public void A_lone_latin_letter_is_never_marked_but_a_lone_kanji_is()
    {
        var known = new KnownWords([Word("en", "I"), Word("zh", "上")]);
        Assert.Null(known.Find("I"));
        Assert.False(known.Find("上"));
    }
}
