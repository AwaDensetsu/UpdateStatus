using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace PlayniteUpdateStatus.UI
{
    public partial class LauncherConnectionCard : UserControl
    {
        public static readonly DependencyProperty StateProperty = DependencyProperty.Register(nameof(State), typeof(LauncherConnectionCardState),
            typeof(LauncherConnectionCard), new PropertyMetadata(null, StateUpdated));
        public static readonly DependencyProperty PollingIntervalProperty = DependencyProperty.Register(nameof(PollingInterval), typeof(int),
            typeof(LauncherConnectionCard), new FrameworkPropertyMetadata(1000, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, PollingUpdated));
        public LauncherConnectionCardState State { get { return (LauncherConnectionCardState)GetValue(StateProperty); } set { SetValue(StateProperty, value); } }
        public int PollingInterval { get { return (int)GetValue(PollingIntervalProperty); } set { SetValue(PollingIntervalProperty, value); } }
        public event EventHandler FolderRequested, SetupRequested, RelaunchRequested, RemoveRequested;
        private bool updatingPolling;
        public LauncherConnectionCard() { InitializeComponent(); ConnectionPanel.DataContext = State; UpdatePolling(); }
        private static void StateUpdated(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            var card = (LauncherConnectionCard)sender;
            if (card.ConnectionPanel != null) card.ConnectionPanel.DataContext = e.NewValue;
        }
        private void FolderClick(object sender, RoutedEventArgs e) { if (State?.ActionsAvailable == true) FolderRequested?.Invoke(this, EventArgs.Empty); }
        private void SetupClick(object sender, RoutedEventArgs e) { if (State?.ActionsAvailable == true && State.ShowSetup) SetupRequested?.Invoke(this, EventArgs.Empty); }
        private void RelaunchClick(object sender, RoutedEventArgs e) { if (State?.ActionsAvailable == true && State.ShowRelaunch) RelaunchRequested?.Invoke(this, EventArgs.Empty); }
        private void RemoveClick(object sender, RoutedEventArgs e) { if (State?.CanRemove == true) RemoveRequested?.Invoke(this, EventArgs.Empty); }
        private static void PollingUpdated(DependencyObject sender, DependencyPropertyChangedEventArgs e) { ((LauncherConnectionCard)sender).UpdatePolling(); }
        private void UpdatePolling()
        {
            if (PollingInput == null) return;
            updatingPolling = true;
            try
            {
                var choices = new List<PollingChoice> { new PollingChoice(500,"Fast — 500 ms"), new PollingChoice(1000,"Balanced — 1000 ms"), new PollingChoice(2000,"Low overhead — 2000 ms") };
                if (PollingInterval != 500 && PollingInterval != 1000 && PollingInterval != 2000)
                    choices.Add(new PollingChoice(PollingInterval, "Custom — " + PollingInterval + " ms"));
                PollingInput.ItemsSource = choices;
                PollingInput.SelectedValue = PollingInterval;
            }
            finally { updatingPolling = false; }
        }
        private void PollingChanged(object sender, SelectionChangedEventArgs e)
        {
            var choice = PollingInput.SelectedItem as PollingChoice;
            if (!updatingPolling && choice != null && choice.Milliseconds >= 500 && choice.Milliseconds <= 2000)
                SetCurrentValue(PollingIntervalProperty, choice.Milliseconds);
        }
        private sealed class PollingChoice
        {
            public int Milliseconds { get; }
            public string Label { get; }
            public PollingChoice(int milliseconds, string label) { Milliseconds = milliseconds; Label = label; }
        }
    }
}
