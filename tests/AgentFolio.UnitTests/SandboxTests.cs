using AgentFolio.Platform.Storage;
using AgentFolio.UnitTests;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Slices.Uploads;

namespace AgentFolio.UnitTests;

/// <summary>
/// s4-04: process-isolated extraction sandbox. Builds the extractor once, then
/// exercises it end-to-end through ExtractionService.RunExtractor / ExtractAsync.
/// </summary>
public sealed class SandboxTests : IDisposable
{
    private static readonly Lazy<string> ExtractorDll = new(BuildExtractor);

    private readonly string _tmp = Directory.CreateTempSubdirectory("sandbox-tests-").FullName;
    private readonly InMemoryObjectStore _store = new();

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
    }

    private static string BuildExtractor()
    {
        var repoRoot = FindRepoRoot();
        var project = Path.Combine(repoRoot, "src", "AgentFolio.Sandbox", "AgentFolio.Sandbox.Extractor", "AgentFolio.Sandbox.Extractor.csproj");
        // CI race (measured, run 29191): UnitTests and E2E run as parallel processes and
        // BOTH lazily build this project to the same bin/Release path — concurrent
        // GenerateDepsFile writes collide (IOException, 8 test failures). A machine-wide
        // named mutex serializes the builds; the second one is an incremental no-op.
        using var gate = new Mutex(initiallyOwned: false, name: "AgentFolio.ExtractorBuildLock");
        gate.WaitOne();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{project}\" -c Release -v q --nologo",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("dotnet build failed to start");
            var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            Assert.True(proc.ExitCode == 0, $"extractor build failed: {output}");
        }
        finally { gate.ReleaseMutex(); }
        return Path.Combine(Path.GetDirectoryName(project)!, "bin", "Release", "net10.0", "AgentFolio.Sandbox.Extractor.dll");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentFolio.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private (int ExitCode, string OutputPath) Run(string inputPath, string outputPath)
    {
        var svc = new ExtractionService(_store, ExtractorDll.Value);
        var (code, _) = svc.RunExtractor(inputPath, outputPath);
        return (code, outputPath);
    }

    // ---- round trips -------------------------------------------------------

    [Fact]
    public void TxtRoundTrip()
    {
        var input = Path.Combine(_tmp, "in.bin");
        var output = Path.Combine(_tmp, "out.txt");
        File.WriteAllBytes(input, "hello sandbox world\nline two"u8.ToArray());
        Assert.Equal(0, Run(input, output).ExitCode);
        Assert.Equal("hello sandbox world\nline two", File.ReadAllText(output));
    }

    [Fact]
    public void MdRoundTrip()
    {
        var input = Path.Combine(_tmp, "in.bin");
        var output = Path.Combine(_tmp, "out.txt");
        File.WriteAllBytes(input, "# Title\n\n- item **bold**\n"u8.ToArray());
        Assert.Equal(0, Run(input, output).ExitCode);
        Assert.Equal("# Title\n\n- item **bold**\n", File.ReadAllText(output));
    }

    [Fact]
    public void PdfFixtureExtraction()
    {
        var input = Path.Combine(_tmp, "in.bin");
        var output = Path.Combine(_tmp, "out.txt");
        File.WriteAllBytes(input, Convert.FromBase64String(MinimalPdfBase64));
        Assert.Equal(0, Run(input, output).ExitCode);
        var text = File.ReadAllText(output);
        Assert.Contains("Hello", text);
        Assert.Contains("Sandbox", text);
    }

    [Fact]
    public void DocxFixtureExtraction()
    {
        var input = Path.Combine(_tmp, "in.bin");
        var output = Path.Combine(_tmp, "out.txt");
        File.WriteAllBytes(input, BuildMinimalDocx("Docx body text here"));
        Assert.Equal(0, Run(input, output).ExitCode);
        Assert.Equal("Docx body text here\n", File.ReadAllText(output));
    }

    [Fact]
    public async Task MalformedInputSurfacesAsError()
    {
        var input = Path.Combine(_tmp, "in.bin");
        var output = Path.Combine(_tmp, "out.txt");
        // PK magic but garbage zip: parses as docx, fails inside the sandbox.
        var badBytes = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0xDE, 0xAD, 0xBE, 0xEF };
        File.WriteAllBytes(input, badBytes);
        const string badKey = "tenants/t1/sessions/s1/bad.docx";
        await _store.PutAsync(badKey, new MemoryStream(badBytes, writable: false), "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

        var svc = new ExtractionService(_store, ExtractorDll.Value);
        string? error = null;
        var key = await svc.ExtractAsync("t1", "s1", badKey, e => error = e);
        Assert.Null(key);
        Assert.NotNull(error);
        Assert.Contains("extraction_failed", error);
        Assert.False(File.Exists(output), "output must not exist after parse failure");
    }

    [Fact]
    public async Task OutputCapTruncationHonored()
    {
        var input = Path.Combine(_tmp, "in.bin");
        var output = Path.Combine(_tmp, "out.txt");
        File.WriteAllBytes(input, Encoding.UTF8.GetBytes(new string('x', 2_000_000)));
        Assert.Equal(0, Run(input, output).ExitCode);
        var bytes = File.ReadAllBytes(output);
        Assert.True(bytes.Length <= ExtractionService.MaxOutputBytes,
            $"output {bytes.Length} bytes exceeds the 1MB cap");
        Assert.EndsWith(AgentFolio.Sandbox.Extractor.Program.TruncationMarker, File.ReadAllText(output));
        await Task.CompletedTask;
    }

    [Fact]
    public void SandboxTrap_NeverReadsOutsideScratch()
    {
        var output = Path.Combine(_tmp, "out.txt");
        var svc = new ExtractionService(_store, ExtractorDll.Value);
        var (code, _) = svc.RunExtractor("/etc/passwd", output);
        Assert.NotEqual(0, code);
        Assert.False(File.Exists(output), "sandbox must not read/write outside the scratch dir");
    }

    [Fact]
    public async Task ExtractAsyncStoresExtractedKey()
    {
        var bytes = "stored upload text"u8.ToArray();
        await _store.PutAsync("tenants/t/sessions/s/abc.txt", new MemoryStream(bytes, writable: false), "text/plain");
        var svc = new ExtractionService(_store, ExtractorDll.Value);
        var key = await svc.ExtractAsync("t", "s", "tenants/t/sessions/s/abc.txt");
        Assert.Equal("tenants/t/sessions/s/extracted/abc.txt", key);
        await using var extracted = await _store.GetAsync(key!);
        Assert.NotNull(extracted);
        using var reader = new StreamReader(extracted!, Encoding.UTF8);
        Assert.Equal("stored upload text", reader.ReadToEnd());
    }

    // ---- fixtures ----------------------------------------------------------

    /// <summary>Hand-crafted minimal 1-page PDF with two Tj text runs.</summary>
    private const string MinimalPdfBase64 =
        "JVBERi0xLjQKMSAwIG9iago8PC9UeXBlL0NhdGFsb2cvUGFnZXMgMiAwIFI+PgplbmRvYmoKMiAwIG9iago8PC9UeXBlL1BhZ2VzL0tpZHNbMyAwIFJdL0NvdW50IDE+PgplbmRvYmoKMyAwIG9iago8PC9UeXBlL1BhZ2UvUGFyZW50IDIgMCBSL01lZGlhQm94WzAgMCA2MTIgNzkyXS9Db250ZW50cyA0IDAgUi9SZXNvdXJjZXM8PC9Gb250PDwgIgpRPPwgRCAwIFI+Pj4+PgplbmRvYmoKNCAwIG9iago8PC9MZW5ndGggNzA+CnN0cmVhbQpCVAovRjEgMjQgVGYKNzIgNzIwIFRkCihIZWxsbykgVGoKRVQKQlQKL0YxIDI0IFRmCjcyIDY4MCBUZAooU2FuZGJveCkgVGoKRVQKZW5kc3RyZWFtCmVuZG9iago1IDAgb2JqCjw8L1R5cGUvRm9udC9TdWJ0eXBlL1R5cGUxL0Jhc2VGb250L0hlbHZldGljYT4+CmVuZG9iagp4cmVmCjAgNgowMDAwMDAwMDAwIDY1NTM1IGYgCnRyYWlsZXIKPDwvU2l6ZSA2L1Jvb3QgMSAwIFI+PgpzdGFydHhyZWYKMTAwCiUlRU9G";

    private static byte[] BuildMinimalDocx(string paragraphText)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("[Content_Types].xml");
            using (var w = new StreamWriter(entry.Open()))
            {
                w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                        "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                        "</Types>");
            }
            var doc = zip.CreateEntry("word/document.xml");
            using (var w = new StreamWriter(doc.Open()))
            {
                w.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                        "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                        "<w:body><w:p><w:r><w:t>" + paragraphText + "</w:t></w:r></w:p></w:body>" +
                        "</w:document>");
            }
        }
        return ms.ToArray();
    }
}
