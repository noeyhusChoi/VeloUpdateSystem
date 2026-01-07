using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace VeloUpdateSystem
{
    public static class AppVersionProvider
    {
        public static string GetVersion()
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>();

            return info?.InformationalVersion ?? "unknown";
        }
    }
}
