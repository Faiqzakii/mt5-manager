using System.ComponentModel;
using System.Windows;
using Mt5Manager.Wpf.ViewModels;
namespace Mt5Manager.Wpf.Views;
public partial class CleanupDialog:Window
{
 public CleanupDialog(CleanupViewModel viewModel){InitializeComponent();DataContext=viewModel;}
 CleanupViewModel Vm=>(CleanupViewModel)DataContext;
 async void Window_Loaded(object sender,RoutedEventArgs e)=>await Vm.LoadPreviewAsync();
 async void Prepare_Click(object sender,RoutedEventArgs e)=>await Vm.PrepareAsync();
 async void Continue_Click(object sender,RoutedEventArgs e)=>await Vm.ContinueAsync(false);
 async void Force_Click(object sender,RoutedEventArgs e)=>await Vm.ContinueAsync(true);
 async void Window_Closing(object? sender,CancelEventArgs e)=>await Vm.CancelPreparationAsync();
}
