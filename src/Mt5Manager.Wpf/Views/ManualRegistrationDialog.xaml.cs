using System.Windows;
using Mt5Manager.Domain.Models;
using Mt5Manager.Wpf.ViewModels;
namespace Mt5Manager.Wpf.Views;
public partial class ManualRegistrationDialog:Window
{
 public ManualRegistrationDialog(){InitializeComponent();DataContext=new ManualRegistrationViewModel();Loaded+=(_,_)=>NameBox.Focus();}
 public TerminalRegistration? Registration{get;private set;}
 void Register_Click(object sender,RoutedEventArgs e){var vm=(ManualRegistrationViewModel)DataContext;if(!vm.IsValid){NameBox.Focus();return;}Registration=vm.CreateRegistration();DialogResult=true;}
 void BrowseExecutable_Click(object sender,RoutedEventArgs e){var dialog=new Microsoft.Win32.OpenFileDialog{Filter="MetaTrader executable|terminal64.exe;terminal.exe|Executables|*.exe",Title="Choose MT5 executable"};if(dialog.ShowDialog(this)==true)((ManualRegistrationViewModel)DataContext).ExecutablePath=dialog.FileName;}
 void BrowseData_Click(object sender,RoutedEventArgs e){var dialog=new Microsoft.Win32.OpenFolderDialog{Title="Choose the existing MT5 data folder"};if(dialog.ShowDialog(this)==true)((ManualRegistrationViewModel)DataContext).DataDirectory=dialog.FolderName;}
 }
