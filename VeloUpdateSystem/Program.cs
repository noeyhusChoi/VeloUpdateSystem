using System;
using Velopack;

namespace VeloUpdateSystem
{
    public static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            VelopackApp.Build()
                .SetArgs(args)
                .Run();

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
    }
}
