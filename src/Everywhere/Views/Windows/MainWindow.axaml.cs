using ShadUI.Controls;

namespace Everywhere.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosed(EventArgs e)
    {
        // MainWindow would be easily closed and reopened.
        // Its content should be null before closing to make it detach from visual tree.
        // Otherwise, it will try to attach to the visual tree again (Exception).
        Content = null;
        base.OnClosed(e);
    }
}