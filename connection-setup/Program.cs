using System;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using PlayniteUpdateStatus.Launchers.Epic;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Alternate administrator credentials must not change another user's
        // protocol/startup registration or write their connection backup.
        if (args.Length != 3 || (args[0] != "enable" && args[0] != "remove") ||
            args[2] != WindowsIdentity.GetCurrent().User.Value) return 1;
        try
        {
            var setup = new EpicConnectionSetup();
            if (args[0] == "remove") setup.Remove();
            else
            {
                string root = EpicLocalAccess.FindInstallationFolder(args[1]);
                if (root == null) return 1;
                setup.Enable(root);
            }
            return 0;
        }
        catch (Exception error)
        {
            while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
            if (error is UnauthorizedAccessException || error.HResult == unchecked((int)0x80070005)) return 5;
            return error is IOException ? 3 : 1;
        }
    }
}
