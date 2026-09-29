using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Glossa.App.Theme;
using Glossa.Core.Config;

namespace Glossa.App.Views;

/// <summary>The word card bound to a <see cref="LookupViewModel"/>, with its own palette (card theme and accent).</summary>
public partial class CardView : UserControl
{
    private readonly ResourceDictionary _palette = new();
    private LookupViewModel? _vm;

    public event Action? SaveRequested;
    public event Action? SpeakRequested;

    public CardView()
    {
        InitializeComponent();
        Resources.MergedDictionaries.Add(_palette);
        DataContextChanged += (_, _) => Attach(DataContext as LookupViewModel);
    }

    /// <summary>Preset (or «Свой»: its layout, width and parts), theme, colours, text size and training mode from the settings.</summary>
    public void ApplyLook(PopupSettings settings, ThemeManager theme)
    {
        ThemeManager.Fill(_palette, Palettes.Card(theme.CardKind(settings.Theme), settings));
        if (_vm is not null)
        {
            var custom = settings.Preset == "custom";
            var layout = custom ? settings.Custom.Base : settings.Preset;
            _vm.Preset = layout is "less" or "more" ? layout : "standard";
            _vm.SetLook(custom ? settings.Custom.Hidden : [], custom ? Math.Clamp(settings.Custom.Width, 360, 800) : null);
            _vm.HideTranslation = settings.HideTranslation;
        }
        var scale = Math.Clamp(settings.FontScale, 0.85, 1.3);
        Root.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(scale, scale);
        RenderContext();
    }

    private void Attach(LookupViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = vm;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        RenderContext();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LookupViewModel.Context) or nameof(LookupViewModel.ContextOffset) or nameof(LookupViewModel.WordLength))
            RenderContext();
    }

    /// <summary>The original line with the word highlighted, in both cards that show it.</summary>
    private void RenderContext()
    {
        Render(ContextStandard);
        Render(ContextMore);
    }

    private void Render(TextBlock target)
    {
        target.Inlines.Clear();
        if (_vm is null) return;
        var text = _vm.Context;
        var off = _vm.ContextOffset;
        var len = _vm.WordLength;
        if (off < 0 || off + len > text.Length)
        {
            target.Inlines.Add(new Run(text));
            return;
        }
        target.Inlines.Add(new Run(text[..off]));
        var word = new Run(text.Substring(off, len));
        word.SetResourceReference(TextElement.BackgroundProperty, "MarkBg");
        word.SetResourceReference(TextElement.ForegroundProperty, "MarkInk");
        target.Inlines.Add(word);
        target.Inlines.Add(new Run(text[(off + len)..]));
    }

    private void OnSave(object sender, RoutedEventArgs e) => SaveRequested?.Invoke();

    private void OnSpeak(object sender, RoutedEventArgs e) => SpeakRequested?.Invoke();

    private void OnToggleDictionaries(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.DictionariesOpen = !_vm.DictionariesOpen;
    }
}
