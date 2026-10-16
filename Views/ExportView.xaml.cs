using System.Windows.Controls;
using Testica.ViewModels;

namespace Testica.Views;

public partial class ExportView : UserControl
{
    public ExportView()
    {
        InitializeComponent();
    }

    private async void FirebaseSyncClick(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not ExportViewModel viewModel) return;
        var password = FirebasePassword.Password;
        FirebasePassword.Clear();
        await viewModel.SynchronizeFirebaseAsync(viewModel.FirebaseEmail, password);
    }
}
