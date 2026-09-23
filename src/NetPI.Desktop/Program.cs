namespace NetPI.Desktop;

internal static class Program
{
    private const string MutexName = @"Local\NetPI.Desktop.SingleInstance";
    internal const string ActivateEventName = @"Local\NetPI.Desktop.Activate";

    [STAThread]
    private static void Main(string[] args)
    {
        // One window per user: a second launch just brings the running window to the front.
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew && !args.Contains("--multi", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
                activate.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // the other instance is still starting up
            }
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args));
    }
}
