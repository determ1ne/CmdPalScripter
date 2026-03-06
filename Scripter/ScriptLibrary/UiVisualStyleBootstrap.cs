using System.Threading;
using System.Windows.Forms;

namespace Scripter.ScriptLibrary;

internal static class UiVisualStyleBootstrap
{
    private static int _initialized;

    public static void EnsureEnabled()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
    }
}
