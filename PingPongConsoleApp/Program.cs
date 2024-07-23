using PingPongConsoleApp;
using System;

class Program {
    static void Main(string[] args) {
        Console.WriteLine("Enter 'server' to start the server or 'client' to start the client:");
        string input = Console.ReadLine();

        if (input.Equals("server", StringComparison.OrdinalIgnoreCase)) {
            var server = new TcpServer();
            server.Start();
        } else if (input.Equals("client", StringComparison.OrdinalIgnoreCase)) {
            var client = new TcpClient();
            client.Start();
        } else {
            Console.WriteLine("Invalid input. Please enter 'server' or 'client'.");
        }
    }
}