using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using FourthIBTracker.Models;

namespace FourthIBTracker.Views;

/// <summary>AttendanceStatus → its legend colour brush.</summary>
public class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is AttendanceStatus s ? new SolidColorBrush(s.ToColor()) : Brushes.Transparent;

    public object ConvertBack(object v, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>AttendanceStatus → display label (palette).</summary>
public class StatusLabelConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value switch
        {
            AttendanceStatus.None => "Clear",
            AttendanceStatus.Loa => "LOA",
            AttendanceStatus.Awol => "AWOL",
            AttendanceStatus s => s.ToString(),
            _ => "",
        };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>AttendanceStatus → its palette hotkey.</summary>
public class StatusKeyConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value switch
        {
            AttendanceStatus.None => "0",
            AttendanceStatus.Present => "1",
            AttendanceStatus.Loa => "2",
            AttendanceStatus.Late => "3",
            AttendanceStatus.Awol => "4",
            _ => "",
        };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}

/// <summary>AttendanceStatus → compact label that fits in a week cell.</summary>
public class StatusShortLabelConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value switch
        {
            AttendanceStatus.None => "",
            AttendanceStatus.Present => "P",
            AttendanceStatus.Loa => "LOA",
            AttendanceStatus.Late => "Late",
            AttendanceStatus.Awol => "AWOL",
            _ => "",
        };

    public object ConvertBack(object v, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}
