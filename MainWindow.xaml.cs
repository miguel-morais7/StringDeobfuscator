using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace StringDeobfuscator;

public partial class MainWindow : Window
{
    private readonly List<DllItem> _dllItems = new();

    public MainWindow()
    {
        InitializeComponent();
    }

    private void BrowseFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select DLL files to deobfuscate",
            Filter = "DLL files (*.dll)|*.dll",
            Multiselect = true
        };

        if (dlg.ShowDialog() == true)
        {
            foreach (var file in dlg.FileNames)
                AddDll(file);
        }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            e.Effects = files.Any(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                ? DragDropEffects.Copy
                : DragDropEffects.None;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        foreach (var file in files)
        {
            if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                AddDll(file);
        }
    }

    private void AddDll(string filePath)
    {
        // Don't add duplicates
        if (_dllItems.Any(d => d.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase)))
            return;

        var item = new DllItem { FilePath = filePath };
        _dllItems.Add(item);

        var card = CreateDllCard(item);
        DllListPanel.Children.Add(card);

        DropPlaceholder.Visibility = Visibility.Collapsed;
        DllListScroller.Visibility = Visibility.Visible;
        PatchButton.IsEnabled = true;
    }

    private Border CreateDllCard(DllItem item)
    {
        var fileName = Path.GetFileName(item.FilePath);

        var removeBtn = new TextBlock
        {
            Text = "\u2715",
            Foreground = BrushFromHex("#666680"),
            FontSize = 14,
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(8, 0, 0, 0)
        };

        var statusIcon = new TextBlock
        {
            Text = "",
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        item.StatusIcon = statusIcon;

        var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textPanel.Children.Add(new TextBlock
        {
            Text = fileName,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = BrushFromHex("#cccccc")
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = item.FilePath,
            FontSize = 11,
            Foreground = BrushFromHex("#666680"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(textPanel, 0);
        Grid.SetColumn(statusIcon, 1);
        Grid.SetColumn(removeBtn, 2);

        grid.Children.Add(textPanel);
        grid.Children.Add(statusIcon);
        grid.Children.Add(removeBtn);

        var card = new Border
        {
            Background = BrushFromHex("#16213e"),
            BorderBrush = BrushFromHex("#0f3460"),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid
        };

        item.Card = card;

        removeBtn.MouseLeftButtonDown += (_, _) =>
        {
            _dllItems.Remove(item);
            DllListPanel.Children.Remove(card);
            if (_dllItems.Count == 0)
            {
                DropPlaceholder.Visibility = Visibility.Visible;
                DllListScroller.Visibility = Visibility.Collapsed;
                PatchButton.IsEnabled = false;
            }
        };

        return card;
    }

    private async void PatchButton_Click(object sender, RoutedEventArgs e)
    {
        PatchButton.IsEnabled = false;
        StatusText.Text = "Deobfuscating...";
        StatusText.Foreground = BrushFromHex("#8888aa");

        // Reset all status icons
        foreach (var item in _dllItems)
        {
            item.StatusIcon!.Text = "";
            item.Card!.BorderBrush = BrushFromHex("#0f3460");
        }

        var outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "deobfuscated");
        Directory.CreateDirectory(outputDir);

        int successCount = 0;
        var results = new List<string>();

        foreach (var item in _dllItems)
        {
            var fileName = Path.GetFileName(item.FilePath);
            var outputPath = Path.Combine(outputDir, fileName);

            try
            {
                var result = await Task.Run(() =>
                    Deobfuscator.Deobfuscate(item.FilePath, outputPath));

                item.StatusIcon!.Text = "\u2714";
                item.StatusIcon.Foreground = BrushFromHex("#2ecc71");
                item.Card!.BorderBrush = BrushFromHex("#2ecc71");
                results.Add($"{Path.GetFileNameWithoutExtension(fileName)}: {result.PatchCount} strings");
                successCount++;
            }
            catch (Exception ex)
            {
                item.StatusIcon!.Text = "\u2718";
                item.StatusIcon.Foreground = BrushFromHex("#e94560");
                item.Card!.BorderBrush = BrushFromHex("#e94560");
                results.Add($"{Path.GetFileNameWithoutExtension(fileName)}: FAILED — {ex.Message}");
            }
        }

        StatusText.Foreground = successCount == _dllItems.Count
            ? BrushFromHex("#53d769")
            : successCount > 0 ? BrushFromHex("#f39c12") : BrushFromHex("#e94560");

        StatusText.Text = $"Done! {string.Join(", ", results)}\nOutput: {outputDir}";
        PatchButton.IsEnabled = true;
    }

    private static SolidColorBrush BrushFromHex(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    private class DllItem
    {
        public required string FilePath { get; init; }
        public TextBlock? StatusIcon { get; set; }
        public Border? Card { get; set; }
    }
}
