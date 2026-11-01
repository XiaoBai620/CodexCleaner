using CodexCleaner.Core;

namespace CodexCleaner;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "Codex 本地清理台", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Cleaner cleaner;
        try { cleaner = new Cleaner(Cleaner.DefaultDataRoot(), Cleaner.DefaultTransactionRoot()); }
        catch (CleanerException e)
        {
            MessageBox.Show(e.Message, "Codex 本地清理台", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Application.Run(new MainForm(cleaner));
    }
}
