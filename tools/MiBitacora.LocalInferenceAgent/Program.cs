using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MiBitacora.LocalInferenceAgent;
using MiBitacora.Services;

if (args.Length != 2 || args[0] != "--config") return 2;
var configPath = Path.GetFullPath(args[1]);
var directory = Path.GetDirectoryName(configPath)!;
var logPath = Path.Combine(directory, "agent.log");
var logLock = new object();
void Log(string message)
{
    lock (logLock)
    {
        if (File.Exists(logPath) && new FileInfo(logPath).Length > 1024 * 1024) File.Move(logPath, logPath + ".previous", true);
        File.AppendAllText(logPath, $"{DateTime.UtcNow:O} {message}{Environment.NewLine}");
    }
}

try
{
    var mutexName = "Local\\MiBitacoraInference-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configPath)))[..16];
    using var mutex = new Mutex(true, mutexName, out var created);
    if (!created) return 0;
    var settings = JsonSerializer.Deserialize<AgentSettings>(await File.ReadAllTextAsync(configPath), BridgeProtocol.Json)
        ?? throw new InvalidDataException();
    _ = settings.ServerUri();
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    using var remote = new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5), MaxConnectionsPerServer = 12
    }) { Timeout = Timeout.InfiniteTimeSpan };
    using var local = new HttpClient(new SocketsHttpHandler
    { AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) }) { Timeout = Timeout.InfiniteTimeSpan };

    if (settings.StartOllama)
    {
        var available = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var response = await local.GetAsync("http://127.0.0.1:11434/api/tags", timeout.Token);
            available = response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
        if (!available && File.Exists(settings.OllamaExe) && Path.GetFileName(settings.OllamaExe).Equals("ollama.exe", StringComparison.OrdinalIgnoreCase))
        {
            var start = new ProcessStartInfo(settings.OllamaExe, "serve")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var (key, value) in new Dictionary<string, string>
            {
                ["OLLAMA_HOST"] = "127.0.0.1:11434", ["OLLAMA_NO_CLOUD"] = "1", ["OLLAMA_CONTEXT_LENGTH"] = "8192",
                ["OLLAMA_NUM_PARALLEL"] = "1", ["OLLAMA_MAX_LOADED_MODELS"] = "2", ["OLLAMA_MAX_QUEUE"] = "8",
                ["OLLAMA_VULKAN"] = "1", ["OLLAMA_IGPU_ENABLE"] = "1", ["OLLAMA_DEBUG_LOG_REQUESTS"] = "false"
            }) start.Environment[key] = value;
            if (!string.IsNullOrWhiteSpace(settings.ModelsPath)) start.Environment["OLLAMA_MODELS"] = settings.ModelsPath;
            var process = Process.Start(start);
            if (process is not null)
            {
                _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
                _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
                Log("Started Ollama on loopback with cloud disabled.");
            }
        }
    }
    Log("Started private inference agent; outgoing HTTPS connections only.");
    await new InferenceAgent(settings, remote, local, Log).RunAsync(stop.Token);
    return 0;
}
catch (OperationCanceledException) { return 0; }
catch (Exception)
{
    Log("Agent stopped. Check private configuration, runtime and connectivity.");
    return 1;
}
