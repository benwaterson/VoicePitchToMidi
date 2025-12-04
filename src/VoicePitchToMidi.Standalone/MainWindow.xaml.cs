using System.Windows;

namespace VoicePitchToMidi.Standalone;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        Closing += (s, e) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.Dispose();
            }
        };
    }
}
