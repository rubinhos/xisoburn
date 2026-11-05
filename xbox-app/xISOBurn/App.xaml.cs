using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace xISOBurn
{
    sealed partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            // No Xbox, apps UWP começam em "modo mouse" (cursor controlado pelo analógico).
            // WhenRequested desliga isso: a navegação fica por foco (direcional + A/B).
            RequiresPointerMode = ApplicationRequiresPointerMode.WhenRequested;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            var rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null)
            {
                rootFrame = new Frame();
                Window.Current.Content = rootFrame;
            }

            if (rootFrame.Content == null)
                rootFrame.Navigate(typeof(MainPage), e.Arguments);

            Window.Current.Activate();
        }
    }
}
