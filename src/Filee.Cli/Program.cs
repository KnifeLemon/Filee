// Entry point of filee-cli.exe (run as "filee" through cli\filee.cmd). Ctrl+C cancels the running conversion or
// stops watching; a second Ctrl+C ends the process at once.

using System.Text;
using Filee.Cli;
using Filee.Core.Settings;

Console.OutputEncoding = Encoding.UTF8;
IDisposable? portable;
try { portable = PortableMode.Initialize(); }
catch (PortableStorageException ex)
{
    Console.Error.WriteLine(new EnglishTexts().Format("startup.portable_unwritable", ex.DataDirectory));
    return 1;
}
using var portableScope = portable;
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    if (cancel.IsCancellationRequested)
        return;
    e.Cancel = true;
    cancel.Cancel();
};
return await Cli.RunAsync(args, Console.Out, Console.Error, cancel.Token);
