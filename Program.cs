using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace SF30thPlayerReader
{
    /// <summary>
    /// Reads lobby player names from SF30thAnniversaryCollection process memory and writes them to text files in the running directory
    /// that can be used to auto-update Twitch overlays.
    /// </summary>
    partial class Program
    {
        static void Main()
        {
            Trace.Listeners.Add(new CustomTraceListener());

            var sf30thProcess = new SF30thProcessMemoryReader();
            var previousNames = new List<string>();

            using var timer = new Timer(_ =>
            {
                if (sf30thProcess.IsBusy)
                    return;

                var playerNames = sf30thProcess.ReadPlayerNames();

                if (playerNames == null)
                    return;

                if (playerNames.SequenceEqual(previousNames))
                {
                    Debug.WriteLine("No change.");
                    return;
                }

                Trace.WriteLine("Players updated.");

                for (var i = 0; i < playerNames.Count; ++i)
                    Trace.WriteLine($"P{i + 1}: {playerNames[i]}");

                WritePlayerNamesToFile(playerNames);
                previousNames = playerNames;
                Debug.WriteLine("Sleeping...");
            }, null, 0, 5000);

            Console.WriteLine("Press [Enter] to quit.");
            Console.ReadLine();
        }

        public static void WritePlayerNamesToFile(List<string> playerNames)
        {
            if (playerNames == null || !playerNames.Any())
                return;

            Debug.WriteLine("Writing player names to files...");
            File.WriteAllText("playersInLobby.txt", string.Join(", ", playerNames));
            File.WriteAllText("p1Name.txt", playerNames[0]);
            File.WriteAllText("p2Name.txt", playerNames.Count > 1 ? playerNames[1] : "");
        }
    }
}
