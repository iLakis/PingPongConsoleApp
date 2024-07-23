using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PingPongClient;
using System.Diagnostics.Tracing;

var serviceProvider = new ServiceCollection()
    .AddLogging(configure => configure.AddConsole())
    .BuildServiceProvider();

var loggerFactory = serviceProvider.GetService<ILoggerFactory>();

string clientLoggerCategory = "TcpClient";
string responseLoggerCategory = "ResponseLogger";
var clientLogger = loggerFactory.CreateLogger(clientLoggerCategory);
var responseLogger = loggerFactory.CreateLogger(responseLoggerCategory);

var client = new TcpClient(clientLogger, responseLogger);

// Enable SSL logging
var listener = new SslEventListener();
var cts = new CancellationTokenSource();
var token = cts.Token;

//var client = new TcpClient();
var clientTask = Task.Run(() => client.StartAsync(token));

Console.WriteLine("Press any key to stop the server...");
Console.ReadKey();

cts.Cancel();
await clientTask;