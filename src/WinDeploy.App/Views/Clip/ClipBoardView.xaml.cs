using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinDeploy.App.ViewModels.Clip;

namespace WinDeploy.App.Views.Clip;

public partial class ClipBoardView : UserControl
{
    public ClipBoardView() => InitializeComponent();

    /// <summary>Click the preview image → open it full size in a separate window.</summary>
    private void OnPreviewImageClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ClipBoardViewModel { Selected: { IsImage: true, Image: { } img } sel })
            new ClipImageWindow(img, sel.Preview) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
}
