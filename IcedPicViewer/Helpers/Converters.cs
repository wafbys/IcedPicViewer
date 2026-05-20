using System.Globalization;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml;

namespace IcedPicViewer.Helpers;

public class LoadingStateToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is Models.LoadingState state && parameter is string states)
        {
            var targetStates = states.Split('|');
            foreach (var targetState in targetStates)
            {
                if (Enum.TryParse<Models.LoadingState>(targetState.Trim(), out var parsed))
                {
                    if (state == parsed) return true;
                }
            }
        }
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool b)
        {
            return b ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
