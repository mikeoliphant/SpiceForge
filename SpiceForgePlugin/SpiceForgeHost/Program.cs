using System;
using AudioPlugSharpHost;

namespace SpiceForgeHost
{
    /// <summary>
    /// Runs the SpiceForge plugin as a stand-alone Windows app, with audio through an ASIO device.
    /// </summary>
    static class Program
    {
        [STAThread]
        static void Main()
        {
            var plugin = new SpiceForgePlugin.SpiceForgePlugin();
            var host = new WindowsFormsHost<SpiceForgePlugin.SpiceForgePlugin>(plugin);

            host.Run();
        }
    }
}
