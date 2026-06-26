using System;

namespace VSIXGoogleChat
{
    public class RealCommandsGenerator
    {
        private static readonly Random _rand = new();

        private static readonly string[] RealCommands =
        [
            "dir",
            "docker ps",
            "docker ps -a",
            "docker images",
            "dotnet --version",
            "netstat -aon | findstr :80",
            "type .gitignore"
        ];

        public static string GenerateRealCommand()
        {
            return RealCommands[_rand.Next(0, RealCommands.Length)];
        }
    }
}
