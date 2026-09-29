using System.Text;
using Glossa.Core.Ocr;

namespace Glossa.Core.Text;

/// <summary>
/// Spelling fixes for Japanese text read by a recognizer trained on Chinese as well: PP-OCRv6 medium reads 撃 as its
/// traditional Chinese twin 擊 and the long vowel mark ー as the kanji 一 (measured 2026-09-29), and neither is found in
/// Japanese dictionaries. Every fix keeps the length, so the boxes of the characters stay where they were.
/// </summary>
public static class JapaneseText
{
    // Old (kyujitai) and traditional Chinese forms, with the form Japanese writes today (the joyo table's old forms
    // plus the traditional twins that differ by a stroke: 內/内, 說/説, 值/値). Only unambiguous ones: 龍, 嶽, 辯 and
    // 著 are left alone, they are written as they are.
    private const string Old =
        "擊內說稅銳悅脫閱步涉黑值眞狀將寢衞槪旣郞卽飮僞爲淸靑敎靜瀨賴德聽惠溫穩隱顏彥產輕徑經騷繩戾淚鎭愼吳娛乘剩亞惡爭淨錄綠緣姬巢單戰彈禪獸齒歲黃橫擴拔髮敍奧燒曉虛戲據劍險檢驗儉硏竝兩滿瀧氣來麥對從縱樂藥營榮螢勞學覺擧譽與寫實賣讀續圖團國會傳轉發廢變戀灣邊關雙雜權觀歡舊當黨爐驛譯擇澤釋鐵體禮豐點區歐毆驅樞應懷壞聲醫鹽萬獵臟藏莊裝壯狹峽挾惱腦嚴參慘處觸獨屬囑濕顯攝盡晝畫碎粹醉齊劑濟齋稱證燈鬪壽鑄擔膽繪繼斷數樓錢淺殘棧踐贊價假曆歷鷄豫餘佛拂疊條稻拜每奬醬渴揭圓缺臺蠶癡聰廳廣效號獻縣辭亂兒勵勸卷囘壘壹孃屆巖帶彌徵恆搖攜晉樣櫻殼沒溪滯濱牀犧盜祕禱竊絕絲緖總纖罐羣肅舍艷莖藝蟲覽讓讚貳軀遞遲鄕鑛陷隨靈飜髓默龜兔鄰麵歸";

    private const string New =
        "撃内説税鋭悦脱閲歩渉黒値真状将寝衛概既郎即飲偽為清青教静瀬頼徳聴恵温穏隠顔彦産軽径経騒縄戻涙鎮慎呉娯乗剰亜悪争浄録緑縁姫巣単戦弾禅獣歯歳黄横拡抜髪叙奥焼暁虚戯拠剣険検験倹研並両満滝気来麦対従縦楽薬営栄蛍労学覚挙誉与写実売読続図団国会伝転発廃変恋湾辺関双雑権観歓旧当党炉駅訳択沢釈鉄体礼豊点区欧殴駆枢応懐壊声医塩万猟臓蔵荘装壮狭峡挟悩脳厳参惨処触独属嘱湿顕摂尽昼画砕粋酔斉剤済斎称証灯闘寿鋳担胆絵継断数楼銭浅残桟践賛価仮暦歴鶏予余仏払畳条稲拝毎奨醤渇掲円欠台蚕痴聡庁広効号献県辞乱児励勧巻回塁壱嬢届巌帯弥徴恒揺携晋様桜殻没渓滞浜床犠盗秘祷窃絶糸緒総繊缶群粛舎艶茎芸虫覧譲讃弐躯逓遅郷鉱陥随霊翻髄黙亀兎隣麺帰";

    private static readonly Dictionary<char, char> Modern = Build();

    private static Dictionary<char, char> Build()
    {
        var map = new Dictionary<char, char>(Old.Length);
        for (var i = 0; i < Old.Length; i++) map.Add(Old[i], New[i]);
        return map;
    }

    /// <summary>The pairs, for the test that the two strings line up.</summary>
    internal static (string Old, string New) Table => (Old, New);

    /// <summary>
    /// A line of Japanese with old forms made modern, compatibility kanji made plain (NFC keeps the length for them)
    /// and the kanji 一 read for the long vowel mark ー put right. Between katakana, or after katakana at the end of a
    /// word, 一 is the mark ("パ一ティ", "スーパ一"); anywhere else it is the number ("もう一つ", "カード一枚", "ケーキ一つ")
    /// unless the kana word with ー is in <paramref name="exists"/> ("ユーザー名", "すごーい").
    /// </summary>
    public static string Normalize(string text, Func<string, bool>? exists = null)
    {
        var chars = text.ToCharArray();
        var changed = false;
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (Modern.TryGetValue(c, out var modern))
            {
                chars[i] = modern;
                changed = true;
            }
            else if (c is >= '豈' and <= '﫿' && c.ToString().Normalize(NormalizationForm.FormC) is { Length: 1 } plain && plain[0] != c)
            {
                chars[i] = plain[0];
                changed = true;
            }
        }
        for (var i = 1; i < chars.Length; i++)
        {
            if (chars[i] != '一' || Scripts.Of(chars[i - 1]) != Script.Kana) continue;
            var next = i + 1 < chars.Length ? chars[i + 1] : ' ';
            var katakanaWord = Katakana(chars[i - 1]) && (Katakana(next) || Scripts.Of(next) is not (Script.Kana or Script.Han));
            if (!katakanaWord && (exists is null || !exists(KanaRunBefore(chars, i) + "ー"))) continue;
            chars[i] = 'ー';
            changed = true;
        }
        return changed ? new string(chars) : text;
    }

    private static bool Katakana(char c) => c is >= 'ァ' and <= 'ヺ' or 'ー' or >= 'ｦ' and <= 'ﾟ';

    /// <summary>Every line of a page, the words inside it split the same way (one character keeps one box).</summary>
    public static OcrPage Normalize(OcrPage page, Func<string, bool>? exists = null)
    {
        List<OcrLine>? lines = null;
        for (var n = 0; n < page.Lines.Count; n++)
        {
            var line = page.Lines[n];
            var text = Normalize(line.Text, exists);
            if (ReferenceEquals(text, line.Text)) continue;
            lines ??= [.. page.Lines];
            lines[n] = line with { Text = text, Words = Resplit(line, text) };
        }
        return lines is null ? page : page with { Lines = lines };
    }

    /// <summary>Kana right before <paramref name="at"/>, the word the mark would lengthen.</summary>
    private static string KanaRunBefore(char[] chars, int at)
    {
        var start = at;
        while (start > 0 && Scripts.Of(chars[start - 1]) == Script.Kana) start--;
        return new string(chars, start, at - start);
    }

    /// <summary>
    /// The words of a line with their text taken from the fixed line. Words are the line's pieces in order, joined as
    /// written (without spaces for kana and kanji); when they do not add up to the line, each word is fixed on its own.
    /// </summary>
    private static IReadOnlyList<OcrWord> Resplit(OcrLine line, string text)
    {
        var words = new List<OcrWord>(line.Words.Count);
        var at = 0;
        foreach (var w in line.Words)
        {
            var found = line.Text.IndexOf(w.Text, at, StringComparison.Ordinal);
            if (found < 0) return line.Words.Select(x => x with { Text = Normalize(x.Text) }).ToList();
            words.Add(w with { Text = text.Substring(found, w.Text.Length) });
            at = found + w.Text.Length;
        }
        return words;
    }
}
