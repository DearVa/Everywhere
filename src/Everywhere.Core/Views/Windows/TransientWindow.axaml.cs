using ShadUI;
using Window = ShadUI.Window;

namespace Everywhere.Views;

public sealed partial class TransientWindow : Window, IReactiveHost
{
    public DialogHost DialogHost => PART_DialogHost;

    public ToastHost ToastHost => PART_ToastHost;

    public TransientWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Its content should be null before closing to make it detach from the visual tree.
        // Otherwise, it will try to attach to the visual tree again (Exception).
        Content = null;
        base.OnClosed(e);
    }
}