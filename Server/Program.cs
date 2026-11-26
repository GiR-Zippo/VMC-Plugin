using XivBoneServer;

/*
 * docker build -t xivbone . && docker run -d --restart unless-stopped -p 40000:40000 xivbone
 */

var port = int.Parse(Environment.GetEnvironmentVariable("PORT") ?? "40000");
var dist = float.Parse(Environment.GetEnvironmentVariable("MAX_DISTANCE") ?? "25",
    System.Globalization.CultureInfo.InvariantCulture);
var debug = Environment.GetEnvironmentVariable("DEBUG") == "1";

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

await new Server(dist, staleSec: 10, debug).RunAsync(port, cts.Token);