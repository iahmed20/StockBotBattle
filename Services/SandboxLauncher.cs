// Services/SandboxLauncher.cs
using System.Diagnostics;
using System.Text;

public class SandboxOptions
{
    public bool Enabled { get; set; } = true;           // false leaves submissions QUEUED
    public string DockerPath { get; set; } = "docker";
    public string Image { get; set; } = "stock-bot-sandbox";
    public string Cpus { get; set; } = "0.5";
    public string Memory { get; set; } = "256m";
    public int PidsLimit { get; set; } = 64;
    public int MaxConcurrentRuns { get; set; } = 10;
    public int StartupTimeoutSeconds { get; set; } = 20; // container start plus loading the strategy
    public int TickTimeoutSeconds { get; set; } = 2;     // time on_tick gets for all symbols
    public int MaxRunMinutes { get; set; } = 60;
    public int MaxOrdersPerTick { get; set; } = 20;
    public int HistoryLength { get; set; } = 50;         // recent prices per symbol sent in each tick
    public int PollIntervalSeconds { get; set; } = 2;
}

// Starts the process that runs one submission; stdin/stdout speak the protocol in sandbox/runner.py
public interface ISandboxLauncher
{
    Process Start(int submissionId);
    Task RemoveAsync(int submissionId); // force-stops that submission's sandbox, if still running
    Task RemoveAllAsync();              // cleans up sandboxes left behind by a previous API process
}

// Thrown when a sandbox can't be started or misbehaves; the message is shown to the user
public class SandboxException : Exception
{
    public SandboxException(string message) : base(message) { }
}

// One locked-down container per submission: no network, read-only root filesystem,
// no capabilities, and CPU, memory, and process-count limits.
public class DockerSandboxLauncher : ISandboxLauncher
{
    private const string Label = "stock-bot-battle.run";

    private readonly SandboxOptions _options;
    private readonly ILogger<DockerSandboxLauncher> _logger;

    public DockerSandboxLauncher(SandboxOptions options, ILogger<DockerSandboxLauncher> logger)
    {
        _options = options;
        _logger = logger;
    }

    private static string ContainerName(int submissionId) => $"stock-bot-run-{submissionId}";

    public Process Start(int submissionId)
    {
        var info = NewStartInfo();
        info.RedirectStandardInput = true;
        foreach (var arg in new[]
        {
            "run", "-i", "--rm",
            "--name", ContainerName(submissionId),
            "--label", $"{Label}={submissionId}",
            "--pull", "never", // only ever run the locally built image
            "--network", "none",
            "--read-only",
            "--tmpfs", "/tmp:rw,noexec,nosuid,size=16m",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--pids-limit", _options.PidsLimit.ToString(),
            "--memory", _options.Memory,
            "--memory-swap", _options.Memory,
            "--cpus", _options.Cpus,
            _options.Image,
        })
            info.ArgumentList.Add(arg);

        try
        {
            return Process.Start(info) ?? throw new SandboxException("Could not start the sandbox.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new SandboxException($"Docker is not available on the server ({ex.Message}).");
        }
    }

    public Task RemoveAsync(int submissionId) => Docker("rm", "-f", ContainerName(submissionId));

    public async Task RemoveAllAsync()
    {
        var ids = await Docker("ps", "-aq", "--filter", $"label={Label}");
        var list = ids.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (list.Length > 0)
            await Docker(new[] { "rm", "-f" }.Concat(list).ToArray());
    }

    private ProcessStartInfo NewStartInfo() => new(_options.DockerPath)
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
        UseShellExecute = false,
    };

    // Runs a short docker command and returns its output; failures are logged, not thrown
    private async Task<string> Docker(params string[] args)
    {
        var info = NewStartInfo();
        foreach (var arg in args) info.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(info)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return await output;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "docker {Args} failed", string.Join(' ', args));
            return "";
        }
    }
}
