using System;
using FormsMessageBox = System.Windows.Forms.MessageBox;

namespace Scripter.ScriptLibrary;

public static class MessageBox
{
    public static int Show(string text)
    {
        return Show(text, "Scripter");
    }

    public static int Show(string text, string caption)
    {
        UiVisualStyleBootstrap.EnsureEnabled();
        return (int)FormsMessageBox.Show(text, caption);
    }
}
