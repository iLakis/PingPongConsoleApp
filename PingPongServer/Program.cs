using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PingPongServer;
using Utils;

var serviceProvider = new ServiceCollection()
    .AddLogging (configure => configure.AddConsole())
    .AddSingleton<PingPongTcpServer>()
    .BuildServiceProvider ();

var loggerFactory = serviceProvider.GetService<ILoggerFactory>();

var logger = loggerFactory.CreateLogger<ILogger<PingPongTcpServer>>();
var server = serviceProvider.GetService<PingPongTcpServer>();
var sslListenerLogger = loggerFactory.CreateLogger<SslEventListener>();
// Enable SSL logging
var listener = new SslEventListener(sslListenerLogger);

var cts = new CancellationTokenSource();
var token = cts.Token;

//var server = new TcpServer();
var serverTask = Task.Run(() => server.StartAsync(token));

Console.WriteLine("Press any key to stop the server...");
Console.ReadKey();

cts.Cancel();
await serverTask;

//Console.WriteLine("Server stopped.");