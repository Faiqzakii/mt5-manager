using System.Windows;
using Mt5Manager.Domain.Models;
using Mt5Manager.Wpf.ViewModels;
namespace Mt5Manager.Wpf.Views;
public partial class ManualRegistrationDialog:Window
{
 public ManualRegistrationDialog(){InitializeComponent();DataContext=new ManualRegistrationViewModel();}
 public TerminalRegistration? Registration{get;private set;}
 void Register_Click(object sender,RoutedEventArgs e){var vm=(ManualRegistrationViewModel)DataContext;if(!vm.IsValid)return;Registration=vm.CreateRegistration();DialogResult=true;}
}
