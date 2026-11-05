using System;
using System.Windows;
using System.Windows.Media;

namespace ST10443277_PROG7312
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void OpenTelemetry_Click(object sender, RoutedEventArgs e)
        {
            LandingView.Visibility = GatewayHeader.Visibility = GatewayFooter.Visibility = CommandView.Visibility = Visibility.Collapsed;
            TelemetryView.Visibility = Visibility.Visible;
            PageContent.Margin = new Thickness(0);
            PageContent.MaxWidth = double.PositiveInfinity;
            Background = new SolidColorBrush(Color.FromRgb(17, 17, 17));
            Title = "Smart-X | Telemetry";
            PageScroll.ScrollToTop();
            TelemetryView.BackButton.Focus();
        }

        private void OpenCommands_Click(object sender, RoutedEventArgs e)
        {
            LandingView.Visibility = GatewayHeader.Visibility = GatewayFooter.Visibility = TelemetryView.Visibility = Visibility.Collapsed;
            CommandView.Visibility = Visibility.Visible;
            PageContent.Margin = new Thickness(0);
            PageContent.MaxWidth = double.PositiveInfinity;
            Background = new SolidColorBrush(Color.FromRgb(244, 247, 250));
            Title = "Smart-X | Commands";
            PageScroll.ScrollToTop();
            CommandView.BackButton.Focus();
        }

        private void BackToGateway_Click(object sender, EventArgs e)
        {
            TelemetryView.Visibility = Visibility.Collapsed;
            CommandView.Visibility = Visibility.Collapsed;
            LandingView.Visibility = GatewayHeader.Visibility = GatewayFooter.Visibility = Visibility.Visible;
            PageContent.Margin = new Thickness(32, 32, 32, 28);
            PageContent.MaxWidth = 1200;
            Background = new SolidColorBrush(Color.FromRgb(244, 247, 250));
            Title = "Smart-X | IoT Gateway";
            PageScroll.ScrollToTop();
            OpenTelemetryButton.Focus();
        }
    }
}
