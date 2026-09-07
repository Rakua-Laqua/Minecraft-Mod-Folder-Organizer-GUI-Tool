using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ModLangOrganizer.Converters;

namespace ModLangOrganizer.Views;

/// <summary>
/// Interaction logic for ModDeployView.xaml
/// </summary>
public partial class ModDeployView : UserControl
{
    private readonly BoolToVisibilityConverter _boolToVisibility = new();
    private readonly HashSet<ItemsControl> _wiredItemControls = new();
    private bool _runtimeUiWired;

    public ModDeployView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_runtimeUiWired)
            return;

        _runtimeUiWired = true;

        var tree = FindVisualChild<TreeView>(this);
        if (tree is not null)
            WireTreeItemVisibility(tree);

        WireDeployStatusUi();
    }

    /// <summary>
    /// 既存XAMLのTreeViewレイアウトを変えず、ViewModel側の検索/無効フォルダ判定を
    /// TreeViewItem.Visibilityへ接続する。子コンテナは展開時に遅延生成されるため、
    /// ItemContainerGeneratorごとに再帰的に監視する。
    /// </summary>
    private void WireTreeItemVisibility(ItemsControl itemsControl)
    {
        if (_wiredItemControls.Add(itemsControl))
        {
            itemsControl.ItemContainerGenerator.StatusChanged += (_, _) =>
            {
                Dispatcher.BeginInvoke(() => WireTreeItemVisibility(itemsControl));
            };
        }

        for (var i = 0; i < itemsControl.Items.Count; i++)
        {
            if (itemsControl.ItemContainerGenerator.ContainerFromIndex(i) is not TreeViewItem item)
                continue;

            BindingOperations.SetBinding(
                item,
                VisibilityProperty,
                new Binding("IsVisible")
                {
                    Converter = _boolToVisibility,
                    Mode = BindingMode.OneWay
                });

            WireTreeItemVisibility(item);
        }
    }

    /// <summary>
    /// モック時に固定文字列だったデプロイボタンへ実行モードを反映し、
    /// 衝突警告と進捗表示を既存アクション領域へ追加する。
    /// </summary>
    private void WireDeployStatusUi()
    {
        var deployLabel = FindVisualChildren<TextBlock>(this)
            .FirstOrDefault(text => string.Equals(
                text.Text,
                "modsフォルダへ反映（同期デプロイ）",
                StringComparison.Ordinal));

        if (deployLabel is null)
            return;

        deployLabel.SetBinding(TextBlock.TextProperty, new Binding("DeployButtonText"));

        var deployButton = FindVisualAncestor<Button>(deployLabel);
        if (deployButton?.Parent is not StackPanel parent)
            return;

        var buttonIndex = parent.Children.IndexOf(deployButton);
        if (buttonIndex < 0)
            return;

        var conflictPanel = BuildConflictPanel();
        parent.Children.Insert(buttonIndex, conflictPanel);
        buttonIndex++;

        var progressPanel = BuildProgressPanel();
        parent.Children.Insert(buttonIndex, progressPanel);
    }

    private Border BuildConflictPanel()
    {
        var panel = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Background = TryFindResource("BgSurfaceBrush") as Brush ?? Brushes.Transparent,
            BorderBrush = TryFindResource("ErrorBrush") as Brush ?? Brushes.IndianRed
        };
        panel.SetBinding(VisibilityProperty, new Binding("HasConflicts") { Converter = _boolToVisibility });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var message = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 10, 0),
            Foreground = TryFindResource("ErrorBrush") as Brush ?? Brushes.IndianRed,
            FontWeight = FontWeights.SemiBold
        };
        message.SetBinding(TextBlock.TextProperty, new Binding("ConflictSummary"));
        grid.Children.Add(message);

        var details = new Button
        {
            Content = "衝突の詳細...",
            Padding = new Thickness(10, 5, 10, 5),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (TryFindResource("DangerButton") is Style dangerStyle)
            details.Style = dangerStyle;
        details.SetBinding(Button.CommandProperty, new Binding("ShowConflictsCommand"));
        Grid.SetColumn(details, 1);
        grid.Children.Add(details);

        panel.Child = grid;
        return panel;
    }

    private FrameworkElement BuildProgressPanel()
    {
        var panel = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 6
        };
        progress.SetBinding(ProgressBar.ValueProperty, new Binding("DeployProgressPercent"));
        panel.Children.Add(progress);

        var status = new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            FontSize = 10.5,
            Foreground = TryFindResource("TextMutedBrush") as Brush ?? Brushes.Gray
        };
        status.SetBinding(TextBlock.TextProperty, new Binding("DeployProgressText"));
        Grid.SetRow(status, 1);
        panel.Children.Add(status);

        return panel;
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in FindVisualChildren<T>(root))
            return child;
        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is null)
            yield break;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
                yield return typed;

            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(start);
        while (current is not null)
        {
            if (current is T typed)
                return typed;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
