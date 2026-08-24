using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using FourthIBTracker.ViewModels;

namespace FourthIBTracker.Views;

public partial class CoursesView : UserControl
{
    private readonly CoursesViewModel _vm;

    public CoursesView(CoursesViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.DataLoaded += BuildColumns;

        // Group rows by section so each section reads as its own block.
        var view = CollectionViewSource.GetDefaultView(vm.Records);
        view.GroupDescriptions.Add(new PropertyGroupDescription("Section"));
        Grid.ItemsSource = view;

        Loaded += async (_, _) =>
        {
            if (_vm.Records.Count == 0 && !_vm.IsLoading)
                await _vm.LoadAsync();
        };
    }

    private static Style DarkHeader(bool wrapped)
    {
        var s = new Style(typeof(DataGridColumnHeader));
        s.Setters.Add(new Setter(Control.BackgroundProperty,
            new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1A))));
        s.Setters.Add(new Setter(Control.ForegroundProperty,
            new SolidColorBrush(Color.FromRgb(0xE8, 0xE6, 0xE3))));
        s.Setters.Add(new Setter(Control.FontSizeProperty, 14.0));
        s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 6, 6, 6)));
        s.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 0)));
        s.Setters.Add(new Setter(Control.BorderBrushProperty,
            new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0))));
        if (wrapped)
        {
            // Vertical course names, reading bottom-up. The DataGrid's
            // ColumnHeaderHeight (set in XAML) leaves room for the longest name.
            var template = new DataTemplate();
            var tb = new FrameworkElementFactory(typeof(TextBlock));
            tb.SetBinding(TextBlock.TextProperty, new Binding());
            tb.SetValue(FrameworkElement.LayoutTransformProperty, new RotateTransform(-90));
            template.VisualTree = tb;
            s.Setters.Add(new Setter(ContentControl.ContentTemplateProperty, template));
            s.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Bottom));
            s.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        }
        else
        {
            s.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Bottom));
        }
        return s;
    }

    /// <summary>Course columns are dynamic (they come from the sheet header), so build them in code.</summary>
    private void BuildColumns()
    {
        Grid.Columns.Clear();
        var flatHeader = DarkHeader(wrapped: false);
        var wrapHeader = DarkHeader(wrapped: true);

        Grid.Columns.Add(new DataGridTextColumn
        { Header = "Name", Binding = new Binding("Name"), Width = 260, MinWidth = 220, HeaderStyle = flatHeader });
        Grid.Columns.Add(new DataGridTextColumn
        { Header = "ACMT", Binding = new Binding("Acmt"), Width = 110, MinWidth = 90, HeaderStyle = flatHeader });
        Grid.Columns.Add(new DataGridTextColumn
        { Header = "Done", Binding = new Binding("CompletedCount"), Width = 90, MinWidth = 75, HeaderStyle = flatHeader });

        var chipBrush = new CourseChipBrushConverter();
        var chipSymbol = new CourseChipSymbolConverter();

        foreach (var course in _vm.CourseNames)
        {
            // A small centred status chip; blank cell = not done.
            var chipText = new FrameworkElementFactory(typeof(TextBlock));
            chipText.SetBinding(TextBlock.TextProperty,
                new Binding($"Courses[{course}]") { Converter = chipSymbol });
            chipText.SetValue(TextBlock.ForegroundProperty,
                new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x24)));
            chipText.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            chipText.SetValue(TextBlock.FontSizeProperty, 13.5);
            chipText.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            chipText.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            var chip = new FrameworkElementFactory(typeof(Border));
            chip.SetBinding(Border.BackgroundProperty,
                new Binding($"Courses[{course}]") { Converter = chipBrush });
            chip.SetValue(FrameworkElement.WidthProperty, 26.0);
            chip.SetValue(FrameworkElement.HeightProperty, 22.0);
            chip.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            chip.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            chip.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            chip.AppendChild(chipText);

            var cellStyle = new Style(typeof(DataGridCell));
            cellStyle.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            cellStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));

            Grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = course,
                CellTemplate = new DataTemplate { VisualTree = chip },
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = 64,
                HeaderStyle = wrapHeader,
                CellStyle = cellStyle,
                CanUserSort = false,
            });
        }
    }
}

public class CourseChipBrushConverter : IValueConverter
{
    private static readonly Brush Complete = new SolidColorBrush(Color.FromRgb(0x8F, 0xBC, 0x72));
    private static readonly Brush Advanced = new SolidColorBrush(Color.FromRgb(0xE8, 0xC1, 0x5A));
    private static readonly Brush NotDone  = new SolidColorBrush(Color.FromRgb(0xB8, 0x5C, 0x5C));

    public object Convert(object value, Type t, object p, CultureInfo c) =>
        (value?.ToString() ?? "").ToLowerInvariant() switch
        {
            "complete" => Complete,
            "advanced" => Advanced,
            "not done" or "" => NotDone,
            _ => Advanced, // any other status shows amber
        };

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}

public class CourseChipSymbolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        (value?.ToString() ?? "").ToLowerInvariant() switch
        {
            "complete" => "✓",
            "advanced" => "★",
            "not done" or "" => "✗",
            _ => "•",
        };

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}
