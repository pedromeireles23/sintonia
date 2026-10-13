using System.Windows;
using System.Windows.Controls;

namespace Sintonia.Desktop;

/// <summary>Detailed template inside dropdown items; compact one for the closed ComboBox selection box.</summary>
public sealed class ComboBoxTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Selected { get; set; }
    public DataTemplate? Dropdown { get; set; }
    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        (container as FrameworkElement)?.TemplatedParent is ComboBoxItem ? Dropdown : Selected;
}
