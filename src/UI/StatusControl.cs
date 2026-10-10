using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Markup;
using System.Windows.Automation;
using Playnite.SDK.Controls;
using Playnite.SDK.Models;
using PlayniteUpdateStatus.Core;

namespace PlayniteUpdateStatus.UI
{
    public sealed class StatusControl : PluginUserControl
    {
        private readonly UpdateMonitor monitor;
        private readonly TextBlock label;
        private readonly ProgressBar bar;
        private readonly TextBlock download;
        private readonly ProgressBar downloadBar;
        private readonly TextBlock metrics;
        private readonly TextBlock title;
        private readonly bool showTitle;
        private readonly bool showControls;
        private readonly Button actionButton;
        private readonly Button closeButton;
        private long actionSession;
        private readonly TextBlock commandMessage;
        private Guid actionGame;
        private UpdateCommand action;
        public bool HasInteractiveControls { get { return showControls && (actionButton.Visibility == Visibility.Visible || closeButton.Visibility == Visibility.Visible); } }
        private readonly Border card;
        private bool suppressed;
        public bool Suppressed { get { return suppressed; } set { suppressed = value; } }
        private static readonly ControlTemplate CloseTemplate = (ControlTemplate)XamlReader.Parse(@"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>
            <Border x:Name='Surface' Background='{TemplateBinding Background}' CornerRadius='4' BorderBrush='{TemplateBinding Foreground}' BorderThickness='0'>
                <Path Data='M 1,1 L 11,11 M 11,1 L 1,11' Width='12' Height='12' Stretch='Fill' Stroke='{TemplateBinding Foreground}' StrokeThickness='1.8' HorizontalAlignment='Center' VerticalAlignment='Center' IsHitTestVisible='False'/>
            </Border>
            <ControlTemplate.Triggers>
                <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Background' Value='#28FFFFFF'/></Trigger>
                <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Surface' Property='BorderThickness' Value='1'/></Trigger>
                <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Surface' Property='Opacity' Value='0.45'/></Trigger>
            </ControlTemplate.Triggers>
            </ControlTemplate>");
        private static readonly ControlTemplate MeterTemplate = (ControlTemplate)XamlReader.Parse(@"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ProgressBar'>
            <Border Background='{TemplateBinding Background}' CornerRadius='3' ClipToBounds='True'><Grid x:Name='PART_Track'><Border x:Name='PART_Indicator' HorizontalAlignment='Left' Background='{TemplateBinding Foreground}' CornerRadius='3'/></Grid></Border>
            <ControlTemplate.Triggers><Trigger Property='IsIndeterminate' Value='True'><Trigger.EnterActions><BeginStoryboard Name='WaitingPulse'><Storyboard><DoubleAnimation Storyboard.TargetName='PART_Indicator' Storyboard.TargetProperty='Opacity' From='0.2' To='0.55' Duration='0:0:0.8' AutoReverse='True' RepeatBehavior='Forever'/></Storyboard></BeginStoryboard></Trigger.EnterActions><Trigger.ExitActions><StopStoryboard BeginStoryboardName='WaitingPulse'/></Trigger.ExitActions></Trigger></ControlTemplate.Triggers>
            </ControlTemplate>");
        private readonly bool global;
        private bool subscribed;
        private bool themeApplied;
        private long revision = -1;
        public StatusControl(UpdateMonitor monitor, bool global = false, bool showTitle = false, bool showControls = false)
        {
            this.monitor = monitor; this.global = global; this.showTitle = showTitle; this.showControls = showControls;
            Focusable = false; IsTabStop = false; IsHitTestVisible = false;
            label = new TextBlock { FontSize = 22, Foreground = Brushes.White, TextAlignment = TextAlignment.Left, TextWrapping = TextWrapping.Wrap };
            download = new TextBlock { FontSize = 20, Foreground = Brushes.White, TextAlignment = TextAlignment.Left, TextWrapping = TextWrapping.Wrap };
            metrics = new TextBlock { FontSize = 15, Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
            ToolTipService.SetIsEnabled(metrics,false);
            title = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Brushes.LightGray, Margin = new Thickness(0,0,0,10), TextWrapping = TextWrapping.Wrap };
            actionButton = new Button { Content = "Pause", MinWidth = 120, Padding = new Thickness(14,6,14,6), Margin = new Thickness(0,12,0,0), HorizontalAlignment = HorizontalAlignment.Left,
                Visibility = Visibility.Collapsed, Focusable = true, IsTabStop = true };
            ToolTipService.SetIsEnabled(actionButton,false);
            closeButton = new Button { Content = "×", Width = 28, Height = 28, Padding = new Thickness(0), FontSize = 20, Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), Foreground = Brushes.LightGray, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Style = new Style(typeof(Button)), Template = CloseTemplate, ToolTip = null, Focusable = true, IsTabStop = true, Visibility = Visibility.Collapsed };
            ToolTipService.SetIsEnabled(closeButton,false);
            AutomationProperties.SetName(closeButton,"Pause downloads and close update status");
            closeButton.Click += (s,e) => { monitor.RequestClose(actionGame,actionSession); Render(monitor.Current); e.Handled = true; };
            commandMessage = new TextBlock { FontSize = 14, Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,8,0,0), Visibility = Visibility.Collapsed };
            actionButton.Click += (s,e) => { monitor.RequestCommand(actionGame,action); e.Handled = true; };
            downloadBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, Margin = new Thickness(0, 6, 0, 12), Focusable = false, Foreground = new SolidColorBrush(Color.FromRgb(71, 133, 195)) };
            bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, Margin = new Thickness(0, 6, 0, 0), Focusable = false };
            bar.Foreground = new SolidColorBrush(Color.FromRgb(121, 188, 51));
            foreach (var meter in new[] { downloadBar, bar }) { meter.Template = MeterTemplate; meter.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)); }
            var panel = new StackPanel();
            if (showControls)
            {
                var header = new Grid { Margin = new Thickness(0,0,0,8) };
                header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                title.Margin = new Thickness(0,0,12,0); header.Children.Add(title); Grid.SetColumn(closeButton,1); header.Children.Add(closeButton); panel.Children.Add(header);
            }
            else if (showTitle) panel.Children.Add(title);
            panel.Children.Add(download); panel.Children.Add(downloadBar); panel.Children.Add(label); panel.Children.Add(bar); panel.Children.Add(metrics);
            if (showControls) { panel.Children.Add(actionButton); panel.Children.Add(commandMessage); }
            card = new Border { Background = new SolidColorBrush(Color.FromArgb(225, 17, 24, 32)), BorderBrush = new SolidColorBrush(Color.FromArgb(70, 214, 177, 111)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(20, 14, 20, 14), Child = panel };
            Content = card;
            Visibility = Visibility.Collapsed;
            Loaded += (s, e) => { ApplyTheme(); if (!subscribed) { monitor.Changed += OnChanged; subscribed = true; } Render(monitor.Current); };
            Unloaded += (s, e) => { if (subscribed) { monitor.Changed -= OnChanged; subscribed = false; } downloadBar.IsIndeterminate = bar.IsIndeterminate = false; };
        }
        public override void GameContextChanged(Game oldContext, Game newContext) { Render(monitor.Current); }
        private void OnChanged(UpdateState state)
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() => { if (subscribed) Render(state); }));
        }
        public void Render(UpdateState state)
        {
            if (state.Revision < revision) return;
            if (!themeApplied) ApplyTheme();
            revision = state.Revision;
            bool visible = !suppressed && !state.Dismissed && state.Visible && (global || GameContext != null && GameContext.Id == state.GameId);
            Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            bool updating = state.Phase == UpdatePhase.Updating;
            title.Text = state.GameName ?? string.Empty; title.Visibility = showTitle && title.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            var display = updating ? state.Display : null;
            download.Text = display == null ? string.Empty : display.DownloadText;
            download.Visibility = download.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            downloadBar.Visibility = download.Visibility;
            bool calm = display != null && (display.Paused || display.WaitingForData);
            downloadBar.IsIndeterminate = !calm && (display == null || !display.DownloadPercent.HasValue);
            downloadBar.Value = display == null ? 0 : display.DownloadPercent ?? 0;
            label.Text = display == null ? state.Text : display.StageText;
            bar.Visibility = state.Phase == UpdatePhase.Updating || state.Phase == UpdatePhase.Ready ? Visibility.Visible : Visibility.Collapsed;
            int? percent = updating ? (display == null ? null : display.WorkPercent) : state.Percent;
            bar.IsIndeterminate = !calm && !percent.HasValue;
            bar.Value = percent ?? 0;
            metrics.Text = display == null ? string.Empty : display.GetMetricsText(monitor.SpeedUnit);
            metrics.ToolTip = null;
            metrics.Visibility = metrics.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            bool commands = showControls && monitor.ControlsEnabled && visible && updating && (state.CanPause || state.CanResume || state.CommandPending);
            actionGame = state.GameId; actionSession = state.SessionId; action = state.CanResume ? UpdateCommand.Resume : UpdateCommand.Pause;
            actionButton.Content = state.CommandPending ? "Please wait…" : state.CanResume ? "Resume" : "Pause";
            actionButton.ToolTip = null;
            closeButton.Visibility = showControls && visible ? Visibility.Visible : Visibility.Collapsed;
            closeButton.IsEnabled = visible && monitor.ControlsEnabled && !state.CommandPending;
            AutomationProperties.SetName(closeButton,"Pause downloads and return to library");
            actionButton.Visibility = commands ? Visibility.Visible : Visibility.Collapsed;
            actionButton.IsEnabled = commands && !state.CommandPending;
            IsHitTestVisible = commands || closeButton.Visibility == Visibility.Visible;
            commandMessage.Text = state.CommandNotice ?? string.Empty;
            commandMessage.Visibility = showControls && visible && commandMessage.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        private void ApplyTheme()
        {
            themeApplied = ThemeResource("FontMain") != null || ThemeResource("Accent") != null;
            foreach (var text in new[] { label, download, metrics, title, commandMessage })
            {
                if (TryFindResource("FontMain") is FontFamily) text.SetResourceReference(TextBlock.FontFamilyProperty, "FontMain");
                else if (ThemeResource("FontMain") is FontFamily) text.FontFamily = (FontFamily)ThemeResource("FontMain");
            }
            if (ThemeResource("TextColor") is Color) { label.Foreground = ThemeBrush("TextColor", 1); download.Foreground = ThemeBrush("TextColor", 1); }
            if (ThemeResource("TextSecondaryColor") is Color) { metrics.Foreground = ThemeBrush("TextSecondaryColor", 0.85); title.Foreground = ThemeBrush("TextSecondaryColor",1); }
            if (ThemeResource("TextColor") is Color) closeButton.Foreground = ThemeBrush("TextColor",1);
            if (ThemeResource("ControlBackgroundColor") is Color) card.Background = ThemeBrush("ControlBackgroundColor", 0.9);
            if (ThemeResource("Accent") is Color) { card.BorderBrush = ThemeBrush("Accent", 0.3); bar.Foreground = ThemeBrush("Accent", 1); downloadBar.Foreground = ThemeBrush("Accent", 0.65); }
            if (ThemeResource("Corner") is CornerRadius) card.CornerRadius = (CornerRadius)ThemeResource("Corner");
        }
        private object ThemeResource(string key)
        {
            // Helper's separate window may not inherit the main theme dictionaries.
            return TryFindResource(key) ?? (Application.Current != null && Application.Current.MainWindow != null ? Application.Current.MainWindow.TryFindResource(key) : null);
        }
        private SolidColorBrush ThemeBrush(string key, double opacity)
        {
            var brush = new SolidColorBrush((Color)ThemeResource(key)) { Opacity = opacity };
            // Snapshot the loaded theme palette; never mutate host brush resources.
            return brush;
        }
    }
}
