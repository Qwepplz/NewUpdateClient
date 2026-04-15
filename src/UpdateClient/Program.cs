using System;
using UpdateClient.App;

namespace UpdateClient
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                return new UpdateClientApplication().Run(args);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
        }
    }
}
