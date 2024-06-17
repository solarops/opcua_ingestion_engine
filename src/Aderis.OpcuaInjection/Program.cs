using Aderis.OpcuaInjection.Helpers;

class Program
{
    static async Task Main(string[] args)
    {
        // await OpcuaSubscribe.Start();

        // System.Environment.Exit(0);

        // Define the server URL
        // string conn1 = "Prosys";
        string conn2 = "Ignition";
        // await OpcuaBrowse.StartBrowse(conn1);
        await OpcuaBrowse.StartBrowse(conn2);
    }
}