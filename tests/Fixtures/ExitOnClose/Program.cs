using System.Text.Json;
using System.Windows.Forms;

var outputPath = args[0];
var closeBehavior = args[1];

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
File.WriteAllText(outputPath, JsonSerializer.Serialize(new
{
    Arguments = args.Skip(2).ToArray(),
    WorkingDirectory = Environment.CurrentDirectory,
    ProcessId = Environment.ProcessId
}));
Application.SetHighDpiMode(HighDpiMode.SystemAware);
Application.EnableVisualStyles();
using var form = new Form { Text = $"ExitOnClose-{Environment.ProcessId}", Width = 100, Height = 100, ShowInTaskbar = true };
if (string.Equals(closeBehavior, "self-exit", StringComparison.Ordinal))
{
    var timer = new System.Windows.Forms.Timer { Interval = 100 };
    timer.Tick += (_, _) => form.Close();
    timer.Start();
}
if (string.Equals(closeBehavior, "ignore", StringComparison.Ordinal))
{
    form.FormClosing += (_, eventArgs) => eventArgs.Cancel = true;
}
Application.Run(form);
