using System.Windows;
using System.Windows.Controls;
using Glossa.App.ViewModels;
using Glossa.Core.Config;

namespace Glossa.App.Views.Settings;

public partial class LanguagesSection : UserControl
{
    private readonly AppServices _services;
    private bool _filled;

    public LanguagesSection(AppServices services, SettingsViewModel model)
    {
        InitializeComponent();
        _services = services;
        DataContext = model;
        ShowCards();
        Loaded += async (_, _) =>
        {
            if (_filled) return;
            _filled = true;
            var engines = await Task.Run(MeasureEngines);
            foreach (var (name, state, ok) in engines) Engines.Children.Add(Line(name, state, ok));
        };
    }

    /// <summary>What each language has now: parsing, level lists, dictionaries (enabled ones, in card order), voice.</summary>
    private void ShowCards()
    {
        var levels = File.Exists(DataPaths.Levels);
        var disabled = _services.Settings.Dictionaries.Disabled;
        string Packs(string lang)
        {
            var titles = _services.Dictionaries.Installed.Where(p => p.SourceLanguage == lang && !disabled.Contains(p.Id)).Select(p => p.Title).ToList();
            return titles.Count > 0 ? string.Join(", ", titles) : "нет — каталог в «Справочниках»";
        }
        string Voice(string lang) => _services.Speech.VoiceFor(lang)?.DisplayName ?? "не установлен";

        LanguageCards.Children.Add(Card("EN", "Английский",
        [
            ("Уровень", levels ? "CEFR A1–C2" : "списки не установлены"), ("Справочники", Packs("en")), ("Голос", Voice("en")),
        ]));
        LanguageCards.Children.Add(Card("JA", "Японский",
        [
            ("Разбор", Directory.Exists(DataPaths.UniDic) ? "UniDic: слова и чтения" : "UniDic не найден"),
            ("Уровень", levels ? "JLPT N5–N1" : "списки не установлены"), ("Справочники", Packs("ja")), ("Голос", Voice("ja")),
        ]));
        LanguageCards.Children.Add(Card("ZH", "Китайский",
        [
            ("Разбор", File.Exists(DataPaths.Cedict) ? "CC-CEDICT: слова и пиньинь" : "CC-CEDICT не найден"),
            ("Уровень", levels ? "HSK 3.0, 1–9" : "списки не установлены"), ("Справочники", Packs("zh")), ("Голос", Voice("zh")),
        ]));
    }

    private static List<(string Name, string State, bool Ok)> MeasureEngines()
    {
        var v5 = Path.Combine(DataPaths.OcrModels, "v5");
        (string, string, bool) Model(string name, string file) =>
            File.Exists(Path.Combine(v5, file)) ? (name, "готово", true) : (name, "нет файла модели", false);
        (string, string, bool) Data(string name, long bytes) => bytes > 0 ? (name, Sizes.Format(bytes), true) : (name, "не найден", false);
        return
        [
            Model("PP-OCRv5 · иероглифы, кана, латиница", "ch_PP-OCRv5_rec_mobile.onnx"),
            Model("PP-OCRv5 · кириллица и латиница", "eslav_PP-OCRv5_rec_mobile.onnx"),
            Data("UniDic · разбор японского", Sizes.Folder(DataPaths.UniDic)),
            Data("CC-CEDICT · слова в китайском", Sizes.File(DataPaths.Cedict)),
        ];
    }

    private static Border Card(string code, string name, (string Label, string Value)[] lines)
    {
        var body = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var codeText = new TextBlock { Text = code, FontSize = 12, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Bottom };
        codeText.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        head.Children.Add(codeText);
        head.Children.Add(new TextBlock { Text = name, FontSize = 17, FontWeight = FontWeights.SemiBold });
        body.Children.Add(head);
        foreach (var (label, value) in lines)
        {
            var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var l = new TextBlock { Text = label, FontSize = 13.5 };
            l.SetResourceReference(TextBlock.ForegroundProperty, "InkSoft");
            var v = new TextBlock { Text = value, FontSize = 13.5, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(v, 1);
            row.Children.Add(l);
            row.Children.Add(v);
            body.Children.Add(row);
        }
        var card = new Border
        {
            Padding = new Thickness(20, 18, 20, 14), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 16, 0), Child = body,
        };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Rule");
        return card;
    }

    private FrameworkElement Line(string name, string state, bool ok)
    {
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 10) };
        var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = row };
        line.SetResourceReference(Border.BorderBrushProperty, "RuleSoft");
        var badge = new ContentControl { Content = state, Style = (Style)FindResource(ok ? "OkText" : "WarnText"), FontSize = 13 };
        DockPanel.SetDock(badge, Dock.Right);
        row.Children.Add(badge);
        row.Children.Add(new TextBlock { Text = name, FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) });
        return line;
    }
}
