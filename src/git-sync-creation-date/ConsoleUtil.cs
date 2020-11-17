using System;

namespace CreationDateSync
{
    public static class ConsoleUtil
    {
        public static void WriteError(string message) => WriteLineConsole("ERROR: " + message, ConsoleColor.Red);

        public static void WriteConsole(string message, ConsoleColor? color = null)
        {
            using (new ConsoleColorSwitcher(color ?? Console.ForegroundColor))
            {
                Console.Write(message);
            }
        }

        public static void WriteLineConsole(string message, ConsoleColor? color = null)
        {
            using (new ConsoleColorSwitcher(color ?? Console.ForegroundColor))
            {
                Console.WriteLine(message);
            }
        }

        public static void WriteNewLine() => Console.WriteLine();

        private class ConsoleColorSwitcher : IDisposable
        {
            public ConsoleColorSwitcher(ConsoleColor color)
            {
                Console.ForegroundColor = color;
            }

            public void Dispose()
            {
                Console.ResetColor();
            }
        }
    }
}