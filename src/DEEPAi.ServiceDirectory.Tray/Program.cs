using System;
using DEEPAi.ServiceDirectory.Infrastructure.Security;

namespace DEEPAi.ServiceDirectory.Tray
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            NativeLibrarySearchPolicy.Apply();

            App application = new App();
            application.InitializeComponent();
            application.Run();
        }
    }
}
