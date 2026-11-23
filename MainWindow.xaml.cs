using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Cliplet;

public partial class MainWindow : Window
{
    readonly ClipStore store;
    IntPtr previousWindow;
    bool ignoreDeactivate;
    bool allowClose;

    public MainWindow(ClipStore store)
    {
        InitializeComponent();
        this.store = store;
        DataContext = store;
        Width = store.WindowWidth;
        Height = store.WindowHeight;
        store.ListReset += RestoreSelection;
    }

    public void PrepareTrayToggle() => ignoreDeactivate = true;

    public void ToggleFromTray()
    {
        if (IsVisible) Hide();
        else ShowPanel();
        Dispatcher.BeginInvoke(() => ignoreDeactivate = false);
    }

    public void ToggleFromHotkey()
    {
        if (IsVisible) Hide();
        else ShowPanel();
    }

    public void ShowPanel()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var ownHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (foreground != IntPtr.Zero && foreground != ownHandle)
            previousWindow = foreground;

        store.RefreshLabels();
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Bottom - Height - 16;
        if (store.Visible.Count > 0)
        {
            ClipList.SelectedItem = store.Visible[0];
            store.SelectedId = store.Visible[0].Id;
        }
        Show();
        PlaceOnScreen();
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    void PlaceOnScreen()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        var width = ActualWidth > 1 ? ActualWidth : Width;
        var height = ActualHeight > 1 ? ActualHeight : Height;
        var left = screen.Right / dpi.DpiScaleX - width - 20;
        var top = screen.Bottom / dpi.DpiScaleY - height - 20;
        var minLeft = screen.Left / dpi.DpiScaleX + 8;
        var minTop = screen.Top / dpi.DpiScaleY + 8;
        Left = Math.Max(minLeft, left);
        Top = Math.Max(minTop, top);
    }

    public void AllowClose() => allowClose = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        store.SaveWindowSize(Width, Height);
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        store.ListReset -= RestoreSelection;
        base.OnClosed(e);
    }

    void Window_Deactivated(object sender, EventArgs e)
    {
        if (ignoreDeactivate) return;
        if (!IsVisible) return;
        store.SaveWindowSize(Width, Height);
        store.Query = "";
        store.ClearChecks();
        Hide();
    }

    void Close_Click(object sender, RoutedEventArgs e) => Hide();

    void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    void ClipList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClipList.SelectedItem is ClipItem item)
            store.SelectedId = item.Id;
        UpdateFormulaPreview();
    }

    void UpdateFormulaPreview()
    {
        if (ClipList.SelectedItem is not ClipItem item || item.Kind != ClipKind.Text || string.IsNullOrWhiteSpace(item.Text))
        {
            TextTools.Visibility = Visibility.Collapsed;
            return;
        }

        TextTools.Visibility = Visibility.Visible;
        var textColor = ((SolidColorBrush)FindResource("TextBrush")).Color;
        var document = FormulaText.BuildDocument(item.Text, textColor, textColor);
        var hasFormula = document != null;
        FormulaView.Document = document;
        FormulaView.Visibility = hasFormula ? Visibility.Visible : Visibility.Collapsed;
        FormulaImageButton.Visibility = hasFormula ? Visibility.Visible : Visibility.Collapsed;
        ((StackPanel)OptimizeButton.Parent).Margin = new Thickness(0, hasFormula ? 6 : 0, 0, 0);
    }

    void Optimize_Click(object sender, RoutedEventArgs e)
    {
        if (ClipList.SelectedItem is ClipItem item)
            store.CopyOptimizedPrompt(item);
    }

    void FormulaImage_Click(object sender, RoutedEventArgs e)
    {
        if (ClipList.SelectedItem is ClipItem item)
            store.CopyFormulaImage(item);
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipItem item)
            store.Delete(item);
        e.Handled = true;
    }

    void Check_Click(object sender, RoutedEventArgs e)
    {
        store.NotifyCheckedChanged();
        e.Handled = true;
    }

    void SelectAll_Click(object sender, RoutedEventArgs e) => store.SelectAllVisible();

    void ClearChecks_Click(object sender, RoutedEventArgs e) => store.ClearChecks();

    void DeleteChecked_Click(object sender, RoutedEventArgs e) => store.DeleteChecked();

    void Pin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipItem item)
            store.TogglePinned(item);
        e.Handled = true;
    }

    void ClipList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<Button>(e.OriginalSource as DependencyObject) != null) return;
        if (FindParent<CheckBox>(e.OriginalSource as DependencyObject) != null) return;
        if (FindParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not ClipItem item) return;
        ClipList.SelectedItem = item;
        store.SelectedId = item.Id;
        store.Copy(item);
    }

    void ClipList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ClipList.SelectedItem is ClipItem item)
            Paste(item);
    }

    void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (e.Key == Key.Escape)
        {
            if (!string.IsNullOrEmpty(store.Query)) store.Query = "";
            else Hide();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter)
        {
            PasteSelected();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Down)
        {
            MoveSelection(1);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Up)
        {
            MoveSelection(-1);
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.P)
        {
            if (ClipList.SelectedItem is ClipItem item) store.TogglePinned(item);
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.Back || e.Key == Key.Delete) && (ctrl || string.IsNullOrEmpty(store.Query)))
        {
            if (store.HasChecked) store.DeleteChecked();
            else if (ClipList.SelectedItem is ClipItem item) store.Delete(item);
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.A && !SearchBox.IsKeyboardFocusWithin)
        {
            store.SelectAllVisible();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == Key.C && SearchBox.SelectionLength == 0)
        {
            if (ClipList.SelectedItem is ClipItem item) store.Copy(item);
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key is >= Key.D1 and <= Key.D9)
        {
            PasteIndex(e.Key - Key.D1);
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key is >= Key.NumPad1 and <= Key.NumPad9)
        {
            PasteIndex(e.Key - Key.NumPad1);
            e.Handled = true;
        }
    }

    void MoveSelection(int delta)
    {
        if (store.Visible.Count == 0) return;
        var index = ClipList.SelectedItem is ClipItem current ? store.Visible.IndexOf(current) : -1;
        if (index < 0) index = delta > 0 ? -1 : 0;
        var next = Math.Clamp(index + delta, 0, store.Visible.Count - 1);
        ClipList.SelectedItem = store.Visible[next];
        store.SelectedId = store.Visible[next].Id;
        ClipList.ScrollIntoView(ClipList.SelectedItem);
    }

    void PasteSelected()
    {
        var item = ClipList.SelectedItem as ClipItem ?? store.Visible.FirstOrDefault();
        if (item != null) Paste(item);
    }

    void PasteIndex(int index)
    {
        if (index < 0 || index >= store.Visible.Count) return;
        Paste(store.Visible[index]);
    }

    void Paste(ClipItem item)
    {
        var target = previousWindow;
        store.Copy(item);
        Hide();
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(90);
            var focused = NativeMethods.ForceForeground(target);
            await Task.Delay(40);
            if (focused && NativeMethods.GetForegroundWindow() == target)
                NativeMethods.SendCtrlV();
            else
                store.Report("已复制。请到目标窗口按 Ctrl+V");
        });
    }

    void RestoreSelection()
    {
        var match = store.Visible.FirstOrDefault(item => item.Id == store.SelectedId) ?? store.Visible.FirstOrDefault();
        ClipList.SelectedItem = match;
        if (match != null)
        {
            store.SelectedId = match.Id;
            ClipList.ScrollIntoView(match);
        }
    }

    static T? FindParent<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match) return match;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }
}
