using System;

namespace FewMoves.Coop.Server
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                ServerApplication.Create(ServerOptions.FromEnvironment(args)).Run();
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("few-moves-coop server start failed: " + exception.GetType().Name);
                return 1;
            }
        }
    }
}
