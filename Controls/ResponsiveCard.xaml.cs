using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using UserControl = System.Windows.Controls.UserControl;

namespace WebViewHub.Controls;

/// <summary>
/// Settings row with icon + title/description + input field. Mimics the
/// look of <c>ui:CardControl</c> but switches to a vertical layout
/// (input wraps under the header) when the card is too narrow to fit
/// everything on one line. Use this in place of <c>ui:CardControl</c>
/// for rows whose input has a non-trivial fixed width (TextBox,
/// ComboBox, NumberBox).
/// </summary>
[ContentProperty(nameof(Field))]
public partial class ResponsiveCard : UserControl
{
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(nameof(Icon), typeof(object), typeof(ResponsiveCard));

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(ResponsiveCard));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(ResponsiveCard),
            new PropertyMetadata(null, OnDescriptionChanged));

    public static readonly DependencyProperty FieldProperty =
        DependencyProperty.Register(nameof(Field), typeof(object), typeof(ResponsiveCard));

    public static readonly DependencyProperty DescriptionVisibilityProperty =
        DependencyProperty.Register(nameof(DescriptionVisibility), typeof(Visibility), typeof(ResponsiveCard),
            new PropertyMetadata(Visibility.Visible));

    /// <summary>
    /// Minimum width the title/description column should keep before the
    /// field wraps under it. Lower = more cramped header before wrap;
    /// higher = wraps sooner to preserve readable header width.
    /// </summary>
    public static readonly DependencyProperty MinHeaderWidthProperty =
        DependencyProperty.Register(nameof(MinHeaderWidth), typeof(double), typeof(ResponsiveCard),
            new PropertyMetadata(160.0));

    public object? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? Title { get => (string?)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Description { get => (string?)GetValue(DescriptionProperty); set => SetValue(DescriptionProperty, value); }
    public object? Field { get => GetValue(FieldProperty); set => SetValue(FieldProperty, value); }
    public Visibility DescriptionVisibility { get => (Visibility)GetValue(DescriptionVisibilityProperty); set => SetValue(DescriptionVisibilityProperty, value); }
    public double MinHeaderWidth { get => (double)GetValue(MinHeaderWidthProperty); set => SetValue(MinHeaderWidthProperty, value); }

    private bool _isCompact;

    public ResponsiveCard()
    {
        InitializeComponent();
    }

    private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ResponsiveCard card) return;
        card.DescriptionVisibility = string.IsNullOrWhiteSpace(e.NewValue as string)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        EvaluateLayout(e.NewSize.Width);
    }

    /// <summary>
    /// Switches between horizontal and compact (wrap-under) layout based
    /// on whether the row can actually fit icon + minimum header + field
    /// on one line at the current width. Beats a fixed pixel threshold —
    /// rows with a wider field (NumberBox+spinners, TextBox+Button) wrap
    /// sooner than rows with a narrow field, automatically.
    /// </summary>
    private void EvaluateLayout(double availableWidth)
    {
        // SizeChanged fires AFTER the measure pass, so DesiredSize on the
        // hosts is already accurate. Avoid calling Measure() manually —
        // it can trigger a re-entrant layout pass that fires SizeChanged
        // again and turns into an oscillation between modes.
        var iconWidth = IconHost.DesiredSize.Width;
        var fieldWidth = FieldHost.DesiredSize.Width;

        // Constants mirror the XAML padding/margin so the math matches
        // what Grid will actually allocate.
        const double cardPaddingHorizontal = 28;   // Border Padding="14,12" → 14+14
        const double iconMarginRight = 14;
        const double fieldMarginLeft = 16;

        var required = cardPaddingHorizontal
            + (iconWidth > 0 ? iconWidth + iconMarginRight : 0)
            + MinHeaderWidth
            + fieldMarginLeft + fieldWidth;

        var compact = availableWidth < required;
        if (compact == _isCompact) return;
        _isCompact = compact;
        ApplyLayout();
    }

    private void ApplyLayout()
    {
        if (_isCompact)
        {
            // Vertical: header on row 0, field on row 1 spanning the header column.
            Grid.SetRow(FieldHost, 1);
            Grid.SetColumn(FieldHost, 1);
            FieldHost.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            FieldHost.Margin = new Thickness(0, 10, 0, 0);
            CompactRow.Height = GridLength.Auto;
            FieldCol.Width = new GridLength(0);
        }
        else
        {
            // Horizontal: field on row 0 column 2, right-aligned.
            Grid.SetRow(FieldHost, 0);
            Grid.SetColumn(FieldHost, 2);
            FieldHost.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
            FieldHost.Margin = new Thickness(16, 0, 0, 0);
            CompactRow.Height = new GridLength(0);
            FieldCol.Width = GridLength.Auto;
        }
    }
}
