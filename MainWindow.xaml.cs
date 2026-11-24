using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Cliplet;

public partial class MainWindow : Window
{
    const double ImagePreviewOpenHeight = 128;
    const double TextToolsOpenHeight = 56;
    const double TextToolsFormulaHeight = 240;

    readonly ClipStore store;
    readonly MessageHost messages;
    readonly Dictionary<string, BitmapImage> previewCache = new(StringComparer.OrdinalIgnoreCase);
    IntPtr previousWindow;
    bool ignoreDeactivate;
    bool allowClose;
    bool imagePreviewOpen;
    bool textToolsOpen;
    string? currentPreviewPath;
    ClipItem? pressedItem;
    int previewEpoch;
    HotkeyCaptureTarget capturingTarget = HotkeyCaptureTarget.None;

    enum HotkeyCaptureTarget { None, Panel, Screenshot }

    public MainWindow(ClipStore store, MessageHost messages)
    {
        InitializeComponent();
        this.store = store;
        this.messages = messages;
        DataContext = store;
        Width = store.WindowWidth;
        Height = store.WindowHeight;
        store.ListReset += RestoreSelection;
        HotkeyCaptureText.Text = messages.HotkeyLabel;
        ScreenshotHotkeyCaptureText.Text = messages.ScreenshotHotkeyLabel;
    }

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        StopHotkeyCapture(restoreLabel: true);
        StartupToggle.IsChecked = LoginStartup.IsEnabled;
        PauseToggle.IsChecked = store.Paused;
        HideOnPasteToggle.IsChecked = store.HideOnPaste;
        HotkeyCaptureText.Text = messages.HotkeyLabel;
        ScreenshotHotkeyCaptureText.Text = messages.ScreenshotHotkeyLabel;
        HotkeyHint.Text = "点击右侧，按 F1 或 Ctrl+Alt+A 等即可设置";
        SettingsPanel.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    void SettingsClose_Click(object sender, RoutedEventArgs e)
    {
        StopHotkeyCapture(restoreLabel: true);
        SettingsPanel.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        LoginStartup.Set(StartupToggle.IsChecked == true);
        store.Report(LoginStartup.IsEnabled ? "已开启开机启动" : "已关闭开机启动");
    }

    void PauseToggle_Click(object sender, RoutedEventArgs e)
    {
        var wantPause = PauseToggle.IsChecked == true;
        if (wantPause != store.Paused)
            store.TogglePaused();
        PauseToggle.IsChecked = store.Paused;
    }

    void HideOnPasteToggle_Click(object sender, RoutedEventArgs e)
    {
        store.HideOnPaste = HideOnPasteToggle.IsChecked == true;
        store.Report(store.HideOnPaste ? "选中后将隐藏窗口" : "选中后保持窗口打开");
    }

    void HotkeyCapture_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindParent<System.Windows.Controls.Button>(source) != null)
            return;
        StartHotkeyCapture(HotkeyCaptureTarget.Panel);
        e.Handled = true;
    }

    void ScreenshotHotkeyCapture_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindParent<System.Windows.Controls.Button>(source) != null)
            return;
        StartHotkeyCapture(HotkeyCaptureTarget.Screenshot);
        e.Handled = true;
    }

    void HotkeyCapture_LostFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        StopHotkeyCapture(restoreLabel: true);

    void ScreenshotHotkeyCapture_LostFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        StopHotkeyCapture(restoreLabel: true);

    void HotkeyClear_Click(object sender, RoutedEventArgs e)
    {
        StopHotkeyCapture(restoreLabel: false);
        ApplyPanelHotkey(NativeMethods.ModControl | NativeMethods.ModAlt, NativeMethods.VkV);
        e.Handled = true;
    }

    void ScreenshotHotkeyClear_Click(object sender, RoutedEventArgs e)
    {
        StopHotkeyCapture(restoreLabel: false);
        ApplyScreenshotHotkey(NativeMethods.ModControl | NativeMethods.ModAlt, NativeMethods.VkA);
        e.Handled = true;
    }

    void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        StopHotkeyCapture(restoreLabel: false);
        var panelDefault = NativeMethods.ModControl | NativeMethods.ModAlt;
        // Avoid mutual clash while restoring both defaults.
        if (messages.HotkeyMods == panelDefault && messages.HotkeyKey == NativeMethods.VkA)
            ApplyPanelHotkey(panelDefault, NativeMethods.VkV);
        if (messages.ScreenshotHotkeyMods == panelDefault && messages.ScreenshotHotkeyKey == NativeMethods.VkV)
            ApplyScreenshotHotkey(panelDefault, NativeMethods.VkA);
        ApplyPanelHotkey(panelDefault, NativeMethods.VkV);
        ApplyScreenshotHotkey(panelDefault, NativeMethods.VkA);
        HotkeyHint.Text = "已恢复默认热键";
        e.Handled = true;
    }

    void StartHotkeyCapture(HotkeyCaptureTarget target)
    {
        StopHotkeyCapture(restoreLabel: true);
        capturingTarget = target;
        messages.HotkeysSuspended = true;
        HotkeyHint.Text = "按下组合键即可设置，Esc 取消";
        if (target == HotkeyCaptureTarget.Panel)
        {
            HotkeyCaptureText.Text = "按下快捷键…";
            HotkeyCaptureBox.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
            HotkeyCaptureBox.Focus();
            Keyboard.Focus(HotkeyCaptureBox);
        }
        else
        {
            ScreenshotHotkeyCaptureText.Text = "按下快捷键…";
            ScreenshotHotkeyCaptureBox.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
            ScreenshotHotkeyCaptureBox.Focus();
            Keyboard.Focus(ScreenshotHotkeyCaptureBox);
        }
    }

    void StopHotkeyCapture(bool restoreLabel)
    {
        if (capturingTarget == HotkeyCaptureTarget.None && !restoreLabel) return;
        var was = capturingTarget;
        capturingTarget = HotkeyCaptureTarget.None;
        messages.HotkeysSuspended = false;
        HotkeyCaptureBox.BorderBrush = (System.Windows.Media.Brush)FindResource("LineBrush");
        ScreenshotHotkeyCaptureBox.BorderBrush = (System.Windows.Media.Brush)FindResource("LineBrush");
        if (restoreLabel)
        {
            HotkeyCaptureText.Text = messages.HotkeyLabel;
            ScreenshotHotkeyCaptureText.Text = messages.ScreenshotHotkeyLabel;
        }
        else if (was == HotkeyCaptureTarget.Panel)
            HotkeyCaptureText.Text = messages.HotkeyLabel;
        else if (was == HotkeyCaptureTarget.Screenshot)
            ScreenshotHotkeyCaptureText.Text = messages.ScreenshotHotkeyLabel;
    }

    void HotkeyCapture_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) =>
        CaptureHotkeyKeyDown(HotkeyCaptureTarget.Panel, HotkeyCaptureText, e);

    void ScreenshotHotkeyCapture_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) =>
        CaptureHotkeyKeyDown(HotkeyCaptureTarget.Screenshot, ScreenshotHotkeyCaptureText, e);

    void CaptureHotkeyKeyDown(HotkeyCaptureTarget target, TextBlock label, System.Windows.Input.KeyEventArgs e)
    {
        if (capturingTarget != target) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.Clear or Key.DeadCharProcessed or Key.ImeProcessed)
        {
            label.Text = FormatHeldModifiers() + "…";
            e.Handled = true;
            return;
        }

        if (key == Key.Escape)
        {
            StopHotkeyCapture(restoreLabel: true);
            HotkeyHint.Text = "已取消";
            e.Handled = true;
            return;
        }

        var mods = ReadModifiersFromKeyboard();
        var vk = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
        {
            e.Handled = true;
            return;
        }

        capturingTarget = HotkeyCaptureTarget.None;
        messages.HotkeysSuspended = false;
        HotkeyCaptureBox.BorderBrush = (System.Windows.Media.Brush)FindResource("LineBrush");
        ScreenshotHotkeyCaptureBox.BorderBrush = (System.Windows.Media.Brush)FindResource("LineBrush");
        if (target == HotkeyCaptureTarget.Panel)
            ApplyPanelHotkey(mods, vk);
        else
            ApplyScreenshotHotkey(mods, vk);
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    static uint ReadModifiersFromKeyboard()
    {
        uint mods = 0;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mods |= NativeMethods.ModControl;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= NativeMethods.ModAlt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= NativeMethods.ModShift;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) mods |= NativeMethods.ModWin;
        return mods;
    }

    static string FormatHeldModifiers()
    {
        var parts = new List<string>();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) parts.Add("Win");
        return parts.Count == 0 ? "" : string.Join("+", parts) + "+";
    }

    void ApplyPanelHotkey(uint mods, uint key)
    {
        if (!messages.TryRegister(mods, key))
        {
            HotkeyCaptureText.Text = messages.HotkeyLabel;
            HotkeyHint.Text = $"无法注册 {MessageHost.Format(mods, key)}，可能已被占用";
            return;
        }

        store.SaveHotkey(messages.HotkeyMods, messages.HotkeyKey);
        store.HotkeyLabel = messages.HotkeyLabel;
        HotkeyCaptureText.Text = messages.HotkeyLabel;
        HotkeyHint.Text = $"打开面板：{messages.HotkeyLabel}";
        store.Report($"打开面板：{messages.HotkeyLabel}");
    }

    void ApplyScreenshotHotkey(uint mods, uint key)
    {
        if (!messages.TryRegisterScreenshot(mods, key))
        {
            ScreenshotHotkeyCaptureText.Text = messages.ScreenshotHotkeyLabel;
            HotkeyHint.Text = $"无法注册 {MessageHost.Format(mods, key)}，可能已被占用";
            return;
        }

        store.SaveScreenshotHotkey(messages.ScreenshotHotkeyMods, messages.ScreenshotHotkeyKey);
        store.ScreenshotHotkeyLabel = messages.ScreenshotHotkeyLabel;
        ScreenshotHotkeyCaptureText.Text = messages.ScreenshotHotkeyLabel;
        HotkeyHint.Text = $"截屏：{messages.ScreenshotHotkeyLabel}";
        store.Report($"截屏热键：{messages.ScreenshotHotkeyLabel}");
    }

    public void StartScreenshotFromHotkey()
    {
        if (IsVisible) Hide();
        messages.HotkeysSuspended = true;
        try
        {
            ScreenCapture.StartRegionSnip();
        }
        finally
        {
            messages.HotkeysSuspended = false;
        }
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

        SettingsPanel.Visibility = Visibility.Collapsed;
        store.MultiSelectMode = false;
        store.RefreshLabels();
        store.Report("点一下粘贴");
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

    void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag) return;
        if (Enum.TryParse<ContentFilter>(tag, out var filter))
            store.SetFilter(filter);
    }

    void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    void ClipList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClipList.SelectedItem is ClipItem item)
            store.SelectedId = item.Id;

        // Defer preview layout until after mouse up, so the click target doesn't shift under the cursor.
        var epoch = ++previewEpoch;
        Dispatcher.BeginInvoke(() =>
        {
            if (epoch != previewEpoch) return;
            UpdateImagePreview();
            UpdateFormulaPreview();
        }, DispatcherPriority.Input);
    }

    void UpdateImagePreview()
    {
        var selected = ClipList.SelectedItem as ClipItem;
        var path = selected?.PreviewPath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            currentPreviewPath = null;
            PreviewImage.BeginAnimation(OpacityProperty, null);
            PreviewImage.Opacity = 1;
            SetPanelOpen(ImagePreview, open: false, ImagePreviewOpenHeight, ref imagePreviewOpen);
            return;
        }

        if (path != currentPreviewPath)
        {
            var pathToShow = path;
            currentPreviewPath = path;
            // Cross-fade when switching between two images while panel stays open.
            if (imagePreviewOpen)
            {
                var fadeOut = new DoubleAnimation(0.15, TimeSpan.FromMilliseconds(90));
                fadeOut.Completed += (_, _) =>
                {
                    if (currentPreviewPath != pathToShow) return;
                    PreviewImage.Source = LoadPreviewBitmap(pathToShow);
                    PreviewImage.BeginAnimation(OpacityProperty,
                        new DoubleAnimation(1, TimeSpan.FromMilliseconds(140))
                        {
                            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                        });
                };
                PreviewImage.BeginAnimation(OpacityProperty, fadeOut);
            }
            else
            {
                PreviewImage.BeginAnimation(OpacityProperty, null);
                PreviewImage.Source = LoadPreviewBitmap(path);
                PreviewImage.Opacity = 1;
            }
        }

        SetPanelOpen(ImagePreview, open: true, ImagePreviewOpenHeight, ref imagePreviewOpen,
            openMargin: new Thickness(14, 2, 14, 6));
    }

    void UpdateFormulaPreview()
    {
        if (ClipList.SelectedItem is not ClipItem item || item.Kind != ClipKind.Text || string.IsNullOrWhiteSpace(item.Text))
        {
            FormulaView.Document = new System.Windows.Documents.FlowDocument();
            SetPanelOpen(TextTools, open: false, TextToolsOpenHeight, ref textToolsOpen);
            return;
        }

        var textColor = ((SolidColorBrush)FindResource("TextBrush")).Color;
        var document = FormulaText.BuildDocument(item.Text, textColor, textColor);
        var hasFormula = document != null;
        FormulaView.Document = document ?? new System.Windows.Documents.FlowDocument();
        FormulaView.Visibility = hasFormula ? Visibility.Visible : Visibility.Collapsed;
        FormulaImageButton.Visibility = hasFormula ? Visibility.Visible : Visibility.Collapsed;
        ((StackPanel)OptimizeButton.Parent).Margin = new Thickness(0, hasFormula ? 6 : 0, 0, 0);

        var target = hasFormula ? TextToolsFormulaHeight : TextToolsOpenHeight;
        SetPanelOpen(TextTools, open: true, target, ref textToolsOpen,
            openMargin: new Thickness(14, 0, 14, 6));
    }

    void SetPanelOpen(FrameworkElement panel, bool open, double openHeight, ref bool isOpen,
        Thickness? openMargin = null)
    {
        if (open == isOpen && open)
        {
            // Already open — gently adjust height if content size changed (e.g. formula).
            if (Math.Abs(panel.MaxHeight - openHeight) > 1)
            {
                panel.BeginAnimation(MaxHeightProperty, new DoubleAnimation(openHeight, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
                });
            }
            return;
        }

        isOpen = open;
        var duration = TimeSpan.FromMilliseconds(open ? 220 : 180);
        var ease = new QuadraticEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn };

        var fromH = double.IsNaN(panel.MaxHeight) || panel.MaxHeight < 0 ? 0 : panel.MaxHeight;
        var toH = open ? openHeight : 0;
        panel.BeginAnimation(MaxHeightProperty, new DoubleAnimation(fromH, toH, duration) { EasingFunction = ease });
        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 1 : 0, duration) { EasingFunction = ease });

        var margin = open
            ? openMargin ?? new Thickness(14, 2, 14, 6)
            : new Thickness(14, 0, 14, 0);
        panel.BeginAnimation(MarginProperty, new ThicknessAnimation(margin, duration) { EasingFunction = ease });

        panel.IsHitTestVisible = open;
    }

    BitmapImage? LoadPreviewBitmap(string path)
    {
        if (previewCache.TryGetValue(path, out var cached)) return cached;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 480;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            previewCache[path] = image;
            return image;
        }
        catch
        {
            return null;
        }
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

    void MultiSelect_Click(object sender, RoutedEventArgs e)
    {
        store.ToggleMultiSelect();
        e.Handled = true;
    }

    void SelectAll_Click(object sender, RoutedEventArgs e) => store.SelectAllVisible();

    void DeleteChecked_Click(object sender, RoutedEventArgs e) => store.DeleteChecked();

    void Pin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClipItem item)
            store.TogglePinned(item);
        e.Handled = true;
    }

    void ContextDelete_Click(object sender, RoutedEventArgs e)
    {
        var item = ContextMenuItem(sender);
        if (item == null) return;
        store.Delete(item);
        e.Handled = true;
    }

    void ContextPin_Click(object sender, RoutedEventArgs e)
    {
        var item = ContextMenuItem(sender);
        if (item == null) return;
        store.TogglePinned(item);
        e.Handled = true;
    }

    static ClipItem? ContextMenuItem(object sender)
    {
        if (sender is not System.Windows.Controls.MenuItem menu) return null;
        if (menu.DataContext is ClipItem direct) return direct;
        if (menu.Parent is System.Windows.Controls.ContextMenu { PlacementTarget: FrameworkElement target }
            && target.DataContext is ClipItem fromTarget)
            return fromTarget;
        return null;
    }

    void ClipList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<Button>(e.OriginalSource as DependencyObject) != null)
        {
            pressedItem = null;
            return;
        }
        if (FindParent<CheckBox>(e.OriginalSource as DependencyObject) != null)
        {
            pressedItem = null;
            return;
        }
        pressedItem = FindParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as ClipItem;
        if (pressedItem != null)
        {
            ClipList.SelectedItem = pressedItem;
            store.SelectedId = pressedItem.Id;
        }
    }

    void ClipList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindParent<Button>(e.OriginalSource as DependencyObject) != null) return;
        if (FindParent<CheckBox>(e.OriginalSource as DependencyObject) != null) return;

        // Use the item from mouse-down — layout may have shifted under the cursor during preview animation.
        var item = pressedItem ?? ClipList.SelectedItem as ClipItem;
        pressedItem = null;
        if (item == null) return;

        ClipList.SelectedItem = item;
        store.SelectedId = item.Id;

        if (store.MultiSelectMode)
        {
            store.SetChecked(item, !item.IsChecked);
            e.Handled = true;
            return;
        }

        // Click = paste back into the window that was focused before the hotkey.
        Paste(item);
        e.Handled = true;
    }

    void ClipList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Single click already pastes; ignore the second click of a double-click.
        e.Handled = true;
    }

    void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (capturingTarget == HotkeyCaptureTarget.Panel)
        {
            HotkeyCapture_PreviewKeyDown(HotkeyCaptureBox, e);
            return;
        }
        if (capturingTarget == HotkeyCaptureTarget.Screenshot)
        {
            ScreenshotHotkeyCapture_PreviewKeyDown(ScreenshotHotkeyCaptureBox, e);
            return;
        }

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
        var hideAfter = store.HideOnPaste;
        ignoreDeactivate = true;
        SettingsPanel.Visibility = Visibility.Collapsed;
        store.Copy(item);
        if (hideAfter)
            Hide();
        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(hideAfter ? 120 : 60);
                if (target != IntPtr.Zero)
                {
                    NativeMethods.ForceForeground(target);
                    await Task.Delay(100);
                    NativeMethods.SendCtrlV();
                    store.Report("已粘贴");
                    if (!hideAfter)
                    {
                        // Bring Cliplet back so user can paste more items.
                        await Task.Delay(80);
                        ignoreDeactivate = true;
                        Show();
                        PlaceOnScreen();
                        Activate();
                        await Task.Delay(120);
                    }
                }
                else
                {
                    store.Report("已复制。请到目标窗口按 Ctrl+V");
                }
            }
            finally
            {
                ignoreDeactivate = false;
            }
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
