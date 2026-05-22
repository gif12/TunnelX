using System.Windows;
using System.Windows.Data;
using AppTunnel.Services;
using WpfControls = System.Windows.Controls;
using FlowDirection = System.Windows.FlowDirection;

namespace AppTunnel.Helpers;

/// <summary>
/// Applies app language flow and Persian text alignment to a visual subtree.
/// Does not break <see cref="FrameworkElement.FlowDirection"/> or <see cref="TextBlock.TextAlignment"/> bindings.
/// </summary>
public static class LocalizationLayoutHelper
{
    public static void ApplyTo(DependencyObject root)
    {
        var loc = LocalizationService.Instance;
        ApplyTo(root, loc.FlowDirection, loc.TextAlignment, new HashSet<DependencyObject>());
    }

    private static void ApplyTo(
        DependencyObject node,
        FlowDirection flow,
        TextAlignment align,
        HashSet<DependencyObject> visited)
    {
        if (!visited.Add(node))
            return;

        if (node is FrameworkElement fe)
        {
            if (ShouldApplyLocalFlowDirection(fe))
                fe.FlowDirection = flow;

            switch (fe)
            {
                case System.Windows.Controls.TextBlock tb when !TextBlockFlags.GetUseEmojiFont(tb):
                    ApplyTextBlockAlignment(tb, align);
                    break;
                case System.Windows.Controls.TextBox box when box.FlowDirection != FlowDirection.LeftToRight:
                    if (!HasBinding(box, System.Windows.Controls.TextBox.TextAlignmentProperty))
                        box.TextAlignment = align;
                    break;
            }
        }

        if (node is WpfControls.Panel or WpfControls.ContentControl or WpfControls.ItemsControl)
        {
            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
                ApplyTo(System.Windows.Media.VisualTreeHelper.GetChild(node, i), flow, align, visited);
        }

        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            ApplyTo(child, flow, align, visited);
    }

    private static bool ShouldApplyLocalFlowDirection(FrameworkElement fe)
    {
        if (HasBinding(fe, FrameworkElement.FlowDirectionProperty))
            return false;

        return fe.ReadLocalValue(FrameworkElement.FlowDirectionProperty) is FlowDirection.RightToLeft;
    }

    private static void ApplyTextBlockAlignment(System.Windows.Controls.TextBlock tb, TextAlignment align)
    {
        if (HasBinding(tb, System.Windows.Controls.TextBlock.TextAlignmentProperty))
            return;

        if (tb.FlowDirection == FlowDirection.LeftToRight)
            return;

        tb.TextAlignment = align;
    }

    private static bool HasBinding(DependencyObject element, DependencyProperty property)
        => BindingOperations.GetBindingExpressionBase(element, property) != null;
}
