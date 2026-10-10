using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Memos.Views;

/// <summary>The draggable strip between the editor and the preview; shows a resize cursor.</summary>
public sealed partial class SplitterHandle : Grid
{
    public SplitterHandle() => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
}

/// <summary>
/// One row of the settings page: a title and description on the left, the control on the right.
/// Its look comes from Themes/Generic.xaml and uses the standard Fluent card brushes.
/// </summary>
public sealed partial class SettingCard : ContentControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingCard), new PropertyMetadata("", OnHeaderChanged));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingCard), new PropertyMetadata("", (d, _) => ((SettingCard)d).UpdateDescription()));

    public SettingCard()
    {
        DefaultStyleKey = typeof(SettingCard);
        IsTabStop = false;
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateDescription();
    }

    /// <summary>An empty description takes no space, so the title stays vertically centred.</summary>
    private void UpdateDescription()
    {
        if (GetTemplateChild("DescriptionText") is TextBlock description)
        {
            description.Visibility = string.IsNullOrEmpty(Description) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        NameContent();
    }

    private static void OnHeaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SettingCard)d).NameContent();

    /// <summary>Screen readers announce the control with the card's title.</summary>
    private void NameContent()
    {
        if (Content is FrameworkElement element && !string.IsNullOrEmpty(Header) && string.IsNullOrEmpty(AutomationProperties.GetName(element)))
        {
            AutomationProperties.SetName(element, Header);
        }
    }
}
