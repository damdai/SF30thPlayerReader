using System;
using System.Diagnostics;

namespace SF30thPlayerReader
{
    public class CustomTraceListener : TextWriterTraceListener
    {
        public override void WriteLine(string message)
        {
            Console.WriteLine($"{DateTime.Now}: {message}");
        }

        public override void WriteLine(string message, string category)
        {
            switch (category.ToLower())
            {
                case "error":
                    Console.ForegroundColor = ConsoleColor.Red;
                    break;
                case "warning":
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    break;
            }

            Console.WriteLine($"{DateTime.Now}: [{category}] {message}");
            Console.ResetColor();
        }
    }
}
