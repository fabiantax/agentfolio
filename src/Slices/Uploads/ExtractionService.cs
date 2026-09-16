using System.Diagnostics;
using System.Text;
using AgentFolio.Platform.Storage;

namespace Slices.Uploads;

/// <summary>
/// Process-spawn extraction sandbox (spec 002 s4-04). Untrusted upload bytes
/// are parsed by a SEPARATE process (AgentFolio.Sandbox.Extractor) so a
/// malicious payload can never reach the API process. The extractor is given
/// exactly two argv paths (scratch input, scratch output) and nothing else.
/// </summary>
public sealed class ExtractionService
{
    public const long MaxInputBytes = UploadService.MaxBytes;
    public const long MaxOutputBytes = 1 * 1024 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    public const string ExtractorEnvVar = "AGENTFOLIO_SANDBOX_EXTRACTOR";

    private static readonly string DefaultExtractorPath =
        Path.Combine(AppContext.BaseDirectory, "sandbox", "AgentFolio.Sandbox.Extractor.dll");

    private readonly IObjectStore _store;
    private readonly string _extractorPath;

    public ExtractionService(IObjectStore store, string? extractorPath = null)
    {
        _store = store;
        _extractorPath = extractorPath
            ?? Environment.GetEnvironmentVariable(ExtractorEnvVar)
            ?? DefaultExtractorPath;
    }

    /// <summary>Object key for extracted text: tenants/{tid}/sessions/{sid}/extracted/{key}.txt.</summary>
    public static string BuildExtractedKey(string uploadKey)
    {
        // tenants/{tid}/sessions/{sid}/{sha256}.ext -> tenants/{tid}/sessions/{sid}/extracted/{sha256}.txt
        var dir = Path.GetDirectoryName(uploadKey)!.Replace('\\', '/');
        var file = Path.GetFileNameWithoutExtension(uploadKey);
        return $"{dir}/extracted/{file}.txt";
    }

    private sealed record ScratchFile(string InputPath, string OutputPath, string Dir);

    /// <summary>
    /// Extracts text for an already-stored upload and stores the result at
    /// BuildExtractedKey(uploadKey). Returns the object key written, or null
    /// when extraction failed (reason logged via <paramref name="onError"/>).
    /// </summary>
    public async Task<string?> ExtractAsync(string tenantId, string sessionId, string uploadKey, Action<string>? onError = null, CancellationToken ct = default)
    {
        string? scratchDir = null;
        try
        {
            await using var stream = await _store.GetAsync(uploadKey, ct);
            if (stream is null)
            {
                onError?.Invoke($"extraction_upload_missing: {uploadKey}");
                return null;
            }
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            if (bytes.LongLength > MaxInputBytes)
            {
                onError?.Invoke($"extraction_input_too_large: {bytes.LongLength}");
                return null;
            }

            // Scratch: fresh temp dir per run; input+output colocated there so
            // the sandbox policy (same-directory argv paths) holds.
            scratchDir = Directory.CreateTempSubdirectory("agentfolio-extract-").FullName;
            var scratch = new ScratchFile(
                Path.Combine(scratchDir, "input.bin"),
                Path.Combine(scratchDir, "output.txt"),
                scratchDir);
            await File.WriteAllBytesAsync(scratch.InputPath, bytes, ct);

            var (exitCode, stderr) = RunExtractor(scratch.InputPath, scratch.OutputPath, ct);
            if (exitCode != 0)
            {
                onError?.Invoke($"extraction_failed: exit={exitCode} stderr={Truncate(stderr, 500)}");
                return null;
            }

            var text = await File.ReadAllTextAsync(scratch.OutputPath, Encoding.UTF8, ct);
            if (Encoding.UTF8.GetByteCount(text) > MaxOutputBytes)
            {
                // Defense in depth: the sandbox truncates, but enforce here too.
                onError?.Invoke("extraction_output_truncated");
                return null;
            }

            var extractedKey = BuildExtractedKey(uploadKey);
            await _store.PutAsync(extractedKey, new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false), "text/plain; charset=utf-8", ct);
            return extractedKey;
        }
        catch (Exception ex)
        {
            onError?.Invoke($"extraction_error: {ex.GetType().Name}: {Truncate(ex.Message, 300)}");
            return null;
        }
        finally
        {
            if (scratchDir is not null)
            {
                try { Directory.Delete(scratchDir, recursive: true); }
                catch { /* scratch cleanup best-effort */ }
            }
        }
    }

    /// <summary>Spawns the extractor process; kills after Timeout.</summary>
    public (int ExitCode, string Stderr) RunExtractor(string inputPath, string outputPath, CancellationToken ct = default)
    {
        // Resolve the CURRENT runtime's dotnet (e.g. ~/.dotnet/dotnet) — 'dotnet' may not
        // be on PATH (measured: extractor never started because /usr/bin/dotnet absent).
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_ROOT") is string root
            ? Path.Combine(root, "dotnet")
            : System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "dotnet";
        var psi = new ProcessStartInfo
        {
            FileName = dotnet,
            Arguments = $"\"{_extractorPath}\" \"{inputPath}\" \"{outputPath}\"",
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // Sandbox env: nothing inherited beyond a minimal PATH.
            EnvironmentVariables =
            {
                ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
                ["HOME"] = Path.GetTempPath(),
                ["DOTNET_NOLOGO"] = "1",
                ["DOTNET_ROOT"] = Path.GetDirectoryName(dotnet) ?? "",
                ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            },
        };

        using var proc = new Process { StartInfo = psi };
        if (!proc.Start())
            return (-1, "extractor_process_failed_to_start");

        var stderrTask = proc.StandardError.ReadToEndAsync();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var exited = WaitForExit(proc, Timeout, ct);
        if (!exited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (-1, "extraction_timeout");
        }

        return (proc.ExitCode, stderrTask.Result + stdoutTask.Result);
    }

    private static bool WaitForExit(Process proc, TimeSpan timeout, CancellationToken ct)
    {
        // Poll so a caller-cancellation still kills the child promptly.
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (proc.HasExited) return true;
            if (ct.IsCancellationRequested) return false;
            Thread.Sleep(50);
        }
        return false;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
