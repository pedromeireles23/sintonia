using System.Windows;
using System.Windows.Controls;

namespace Sintonia.Desktop;

/// <summary>Scrollable, selectable preview for actions whose parameters must remain fully visible.</summary>
internal static class ActionConfirmation
{
    public static bool Show(Window owner, string title, string details, string confirmLabel)
    {
        var dialog = new Window { Owner = owner, Title = title, Width = 850, Height = 640, MinWidth = 600, MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = owner.FontFamily, FontSize = 14 };
        var grid = new Grid { Margin = new Thickness(20) }; grid.RowDefinitions.Add(new()); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var text = new TextBox { Text = details, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        grid.Children.Add(text);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Voltar", IsCancel = true, Padding = new Thickness(16, 8, 16, 8) };
        var confirm = new Button { Content = confirmLabel, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(10, 0, 0, 0) };
        confirm.Click += (_, _) => dialog.DialogResult = true; actions.Children.Add(cancel); actions.Children.Add(confirm); Grid.SetRow(actions, 1); grid.Children.Add(actions);
        dialog.Content = grid; return dialog.ShowDialog() == true;
    }
}
