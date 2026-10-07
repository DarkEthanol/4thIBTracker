using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using FourthIBTracker.ViewModels;

namespace FourthIBTracker.Views;

public partial class OtherCoursesView : UserControl
{
    private readonly OtherCoursesViewModel _viewModel;
    private DataGridColumnHeader? _hoveredHeader;

    public OtherCoursesView(OtherCoursesViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.DataLoaded += BuildColumns;
        Grid.MouseMove += CoursesGrid_MouseMove;
        Grid.MouseLeave += (_, _) => SetHoveredColumn(null);

        var view = CollectionViewSource.GetDefaultView(viewModel.Records);
        view.GroupDescriptions.Add(new PropertyGroupDescription("Section"));
        Grid.ItemsSource = view;

        Loaded += async (_, _) =>
        {
            if (_viewModel.Tabs.Count == 0 && !_viewModel.IsLoading)
                await _viewModel.LoadAsync();
        };
    }

    private void CoursesGrid_MouseMove(object sender, MouseEventArgs e)
    {
        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        SetHoveredColumn(cell?.Column);
    }

    private void SetHoveredColumn(DataGridColumn? column)
    {
        var header = column is null
            ? null
            : FindVisualChildren<DataGridColumnHeader>(Grid)
                .FirstOrDefault(candidate => candidate.Column == column);
        if (ReferenceEquals(header, _hoveredHeader)) return;

        if (_hoveredHeader is not null)
        {
            _hoveredHeader.ClearValue(Control.BackgroundProperty);
            _hoveredHeader.ClearValue(Control.BorderBrushProperty);
        }

        _hoveredHeader = header;
        if (_hoveredHeader is null) return;
        _hoveredHeader.Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x5F, 0x3A));
        _hoveredHeader.BorderBrush = new SolidColorBrush(Color.FromRgb(0x7A, 0x9E, 0x5F));
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = current is ContentElement content
                ? ContentOperations.GetParent(content) ??
                  (content as FrameworkContentElement)?.Parent
                : VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static Style DarkHeader(bool wrapped)
    {
        var style = new Style(typeof(DataGridColumnHeader));
        style.Setters.Add(new Setter(Control.BackgroundProperty,
            new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1A))));
        style.Setters.Add(new Setter(Control.ForegroundProperty,
            new SolidColorBrush(Color.FromRgb(0xE8, 0xE6, 0xE3))));
        style.Setters.Add(new Setter(Control.FontSizeProperty, 14.0));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 0)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty,
            new SolidColorBrush(Color.FromArgb(0x33, 0, 0, 0))));
        if (wrapped)
        {
            var template = new DataTemplate();
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding());
            text.SetValue(FrameworkElement.LayoutTransformProperty, new RotateTransform(-90));
            template.VisualTree = text;
            style.Setters.Add(new Setter(ContentControl.ContentTemplateProperty, template));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty,
                VerticalAlignment.Bottom));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,
                HorizontalAlignment.Center));
        }
        else
        {
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty,
                VerticalAlignment.Bottom));
        }
        return style;
    }

    private void BuildColumns()
    {
        Grid.Columns.Clear();
        var flatHeader = DarkHeader(wrapped: false);
        var verticalHeader = DarkHeader(wrapped: true);

        Grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Name", Binding = new Binding("Name"), Width = 260,
            MinWidth = 220, HeaderStyle = flatHeader,
        });
        Grid.Columns.Add(new DataGridTextColumn
        {
            Header = "ACMT", Binding = new Binding("Acmt"), Width = 110,
            MinWidth = 90, HeaderStyle = flatHeader,
        });
        Grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Done", Binding = new Binding("CompletedCount"), Width = 90,
            MinWidth = 75, HeaderStyle = flatHeader,
        });

        var chipBrush = new CourseChipBrushConverter();
        var chipSymbol = new CourseChipSymbolConverter();
        foreach (var course in _viewModel.CourseNames)
        {
            var chipText = new FrameworkElementFactory(typeof(TextBlock));
            chipText.SetBinding(TextBlock.TextProperty,
                new Binding($"Courses[{course}]") { Converter = chipSymbol });
            chipText.SetValue(TextBlock.ForegroundProperty,
                new SolidColorBrush(Color.FromRgb(0x1E, 0x21, 0x24)));
            chipText.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            chipText.SetValue(TextBlock.FontSizeProperty, 13.5);
            chipText.SetValue(FrameworkElement.HorizontalAlignmentProperty,
                HorizontalAlignment.Center);
            chipText.SetValue(FrameworkElement.VerticalAlignmentProperty,
                VerticalAlignment.Center);

            var chip = new FrameworkElementFactory(typeof(Border));
            chip.SetBinding(Border.BackgroundProperty,
                new Binding($"Courses[{course}]") { Converter = chipBrush });
            chip.SetValue(FrameworkElement.WidthProperty, 26.0);
            chip.SetValue(FrameworkElement.HeightProperty, 22.0);
            chip.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
            chip.SetValue(FrameworkElement.HorizontalAlignmentProperty,
                HorizontalAlignment.Center);
            chip.SetValue(FrameworkElement.VerticalAlignmentProperty,
                VerticalAlignment.Center);
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
                HeaderStyle = verticalHeader,
                CellStyle = cellStyle,
                CanUserSort = false,
            });
        }
    }
}
