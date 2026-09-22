using Microsoft.Win32;
using System.Windows;
using Mt5Manager.Wpf.ViewModels;

namespace Mt5Manager.Wpf.Views;

public partial class Mt5PackageInstallDialog : Window
{
    readonly Mt5PackageInstallViewModel viewModel;
    readonly CancellationTokenSource lifetime=new();

    public Mt5PackageInstallDialog(Mt5PackageInstallViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel=viewModel;
        DataContext=viewModel;
        Closed+=(_,_)=>{lifetime.Cancel();lifetime.Dispose();};
    }

    void ChooseFile_Click(object sender,RoutedEventArgs e)
    {
        var picker=new OpenFileDialog{Title="Choose compiled MT5 package",Filter="Compiled MT5 package (*.ex5)|*.ex5",DefaultExt=".ex5",CheckFileExists=true,Multiselect=false};
        if(picker.ShowDialog(this)==true)viewModel.SourcePath=picker.FileName;
    }

    async void Install_Click(object sender,RoutedEventArgs e)
    {
        try{await viewModel.InstallAsync(lifetime.Token);}
        catch(OperationCanceledException)when(lifetime.IsCancellationRequested){}
    }
}