using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Neo.App.Pages;

namespace Neo.App;

public sealed partial class MainWindow
{
    private ContentDialog? _networkDetailsDialog;

    private void UpdateSettingsGroupVisuals()
    {
        SettingsCaret.Text = _settingsExpanded ? "" : "";
        foreach (var button in SettingsRevealContent.Children.OfType<Button>())
        {
            var selected = _currentPage == "settings" && button.Tag as string == _pageSettings?.OpenGroup;
            button.Background = ThemeUtil.Get(selected ? "NeoAccentSoft" : "NeoChipBg", Microsoft.UI.Colors.Transparent);
            button.Foreground = ThemeUtil.Get(selected ? "NeoAccent" : "NeoText2", Microsoft.UI.Colors.Gray);
            var key = button.Tag as string ?? "general";
            var (label, icon) = key switch
            {
                "network" => ("网络", "\uE774"), "kernel" => ("内核", "\uE950"),
                "routing" => ("分流", "\uE8FD"), "logs" => ("日志", "\uE9D5"),
                "about" => ("关于", "\uE946"), _ => ("通用", "\uE80F")
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label);
            // A selection changes colors/counts, not the six pills' XAML trees.
            // Rebuilding all 18 labels here used to compete with the expansion.
            var row = button.Content as Grid;
            if (row == null || row.Children.Count != 3 || row.Children[0] is not TextBlock)
            {
                row = new Grid { ColumnSpacing = 6 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(Ui.AdaptiveText.WithSize(new TextBlock { FontFamily = new FontFamily("Segoe Fluent Icons"), VerticalAlignment = VerticalAlignment.Center }, 12));
                var title = Ui.AdaptiveText.WithSize(new TextBlock { VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap }, 11.5);
                var count = Ui.AdaptiveText.WithSize(new TextBlock { VerticalAlignment = VerticalAlignment.Center }, 9.5);
                Grid.SetColumn(title, 1); row.Children.Add(title); Grid.SetColumn(count, 2); row.Children.Add(count);
                button.Content = row;
            }
            ((TextBlock)row.Children[0]).Text = icon;
            ((TextBlock)row.Children[0]).Foreground = ThemeUtil.Get(selected ? "NeoAccent" : "NeoText3", Microsoft.UI.Colors.Gray);
            ((TextBlock)row.Children[1]).Text = label;
            ((TextBlock)row.Children[1]).Foreground = button.Foreground;
            ((TextBlock)row.Children[2]).Text = _pageSettings?.GroupItemCount(key).ToString() ?? "";
            ((TextBlock)row.Children[2]).Foreground = ThemeUtil.Get(selected ? "NeoAccent" : "NeoText3", Microsoft.UI.Colors.Gray);
            var narrow = Root.ActualWidth > 0 && Root.ActualWidth <= 1060;
            ((TextBlock)row.Children[2]).Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            row.ColumnSpacing = narrow ? 5 : 6;
            button.Padding = new Thickness(narrow ? 8 : 11, 0, narrow ? 8 : 11, 0);
            button.BorderThickness = new Thickness(1);
            button.BorderBrush = ThemeUtil.Get(selected ? "NeoAccentLine" : "NeoHairline", Microsoft.UI.Colors.Transparent);
        }
    }

    private async Task ShowNetworkDetailsAsync()
    {
        if (_networkDetailsDialog != null || App.IsShuttingDown || Root.XamlRoot == null) return;
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
        var anchor = _currentPage == "conn" ? _pageConn?.DetailsAnchor : focused;
        var details = new NetworkDiagnosticsPage
        {
            Width = Math.Max(300, Math.Min(758, Root.ActualWidth - 50)),
            Height = Math.Max(300, Math.Min(778, Root.ActualHeight - 50))
        };
        var surface = ThemeUtil.Get("NeoPopupSurface", Microsoft.UI.Colors.Black);
        if (surface is SolidColorBrush colour)
        {
            var opaque = colour.Color; opaque.A = 255; surface = new SolidColorBrush(opaque);
        }
        var dialog = new Ui.CapsuleDialog
        {
            Content = details, Anchor = anchor, XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme,
            CornerRadius = new CornerRadius(26), Padding = new Thickness(0),
            Background = surface
        };
        dialog.Resources["ContentDialogMaxWidth"] = 760d;
        dialog.Resources["ContentDialogMaxHeight"] = 780d;
        dialog.Resources["ContentDialogTopOverlay"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        dialog.Resources["ContentDialogSeparatorThickness"] = new Thickness(0);
        dialog.Resources["ContentDialogPadding"] = new Thickness(0);
        dialog.Resources["ContentDialogCornerRadius"] = new CornerRadius(26);
        dialog.Resources["ContentDialogBackground"] = ThemeUtil.Get("NeoPopupSurface", Microsoft.UI.Colors.Black);
        _networkDetailsDialog = dialog;
        details.BackRequested += (_, _) => dialog.Hide();
        void ResizeDetails(object sender, SizeChangedEventArgs e)
        {
            details.Width = Math.Max(280, Math.Min(758, Root.ActualWidth - 50));
            details.Height = Math.Max(240, Math.Min(778, Root.ActualHeight - 50));
        }
        Root.SizeChanged += ResizeDetails;
        try { await Ui.DialogGuard.ShowAsync(dialog); }
        catch (Exception ex) { ShowStatusText("打开连通性详情失败：" + ex.GetType().Name); }
        finally
        {
            Root.SizeChanged -= ResizeDetails;
            details.Release(); _networkDetailsDialog = null;
            if (!App.IsShuttingDown) (focused ?? anchor as Control)?.Focus(FocusState.Programmatic);
        }
    }
}
