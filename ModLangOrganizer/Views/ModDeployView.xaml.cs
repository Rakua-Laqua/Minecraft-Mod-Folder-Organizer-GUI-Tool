using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace ModLangOrganizer.Views;

/// <summary>
/// Interaction logic for ModDeployView.xaml
/// </summary>
public partial class ModDeployView : UserControl
{
    private readonly HashSet<ItemsControl> _wiredTreeLevels = new();
    private IValueConverter? _boolToVisibilityConverter;

    public ModDeployView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _boolToVisibilityConverter ??= TryFindResource("BoolToVis") as IValueConverter;
        if (_boolToVisibilityConverter is null)
            return;

        var tree = FindVisualChild<TreeView>(this);
        if (tree is not null)
            WireNestedTreeVisibility(tree);
    }

    /// <summary>
    /// TreeView.ItemContainerStyle はトップレベルのコンテナに対して適用されるため、
    /// 遅延生成される子階層の TreeViewItem にも IsVisible のバインドを明示する。
    /// </summary>
    private void WireNestedTreeVisibility(ItemsControl itemsControl)
    {
        if (_wiredTreeLevels.Add(itemsControl))
        {
            itemsControl.ItemContainerGenerator.StatusChanged += (_, _) =>
            {
                _ = Dispatcher.InvokeAsync(() => WireNestedTreeVisibility(itemsControl));
            };
        }

        for (var index = 0; index < itemsControl.Items.Count; index++)
        {
            if (itemsControl.ItemContainerGenerator.ContainerFromIndex(index) is not TreeViewItem item)
                continue;

            BindingOperations.SetBinding(
                item,
                VisibilityProperty,
                new Binding("IsVisible")
                {
                    Converter = _boolToVisibilityConverter,
                    Mode = BindingMode.OneWay
                });

            WireNestedTreeVisibility(item);
        }
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
                return typed;

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
                return descendant;
        }

        return null;
    }
}
