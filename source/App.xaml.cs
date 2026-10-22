using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Core;
using Windows.UI.ViewManagement;

namespace GoW3Installer
{
    sealed partial class App : Application
    {
        public App() { InitializeComponent(); }
        bool systemBackRegistered;

        void RegisterSystemBack()
        {
            if (systemBackRegistered) return;
            // Consume system Back (B button on Xbox gamepad) so B does not exit the launcher.
            SystemNavigationManager.GetForCurrentView().BackRequested += OnSystemBack;
            systemBackRegistered = true;
        }

        void OnSystemBack(object sender, BackRequestedEventArgs args)
        {
            args.Handled = true;
        }

        protected override void OnActivated(IActivatedEventArgs args)
        {
            if (args.Kind != ActivationKind.Protocol) return;
            var protocol = (ProtocolActivatedEventArgs)args;
            if (protocol.Uri.Scheme != "uwp-gow3-launcher") return;
            RegisterSystemBack();
            var page = Window.Current.Content as MainPage;
            if (page == null)
            {
                page = new MainPage();
                Window.Current.Content = page;
            }
            Window.Current.Activate();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            ApplicationView.GetForCurrentView().SetDesiredBoundsMode(ApplicationViewBoundsMode.UseVisible);
            RegisterSystemBack();
            Window.Current.Content = new MainPage();
            Window.Current.Activate();
        }
    }
}
