using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;

[assembly: AssemblyTitle("Synthetic WoW Forever verification fixture")]
[assembly: AssemblyProduct("World of Warcraft")]
[assembly: AssemblyVersion("1.60.1.0")]
[assembly: AssemblyFileVersion("1.60.1.12345")]

internal static class SyntheticGame
{
    private static void Main(string[] args)
    {
        if (args.Length != 1) return;
        TcpListener listener = new TcpListener(IPAddress.Loopback, Int32.Parse(args[0]));
        listener.Start();
        Thread.Sleep(Timeout.Infinite);
    }
}
