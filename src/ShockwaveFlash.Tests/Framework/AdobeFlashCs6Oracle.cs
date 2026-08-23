using System.Diagnostics;
using System.Text;

namespace ShockwaveFlash.Tests.Framework;

internal sealed record AdobeFlashCs6OracleResult(
    byte[] Swf,
    string CompilerPath,
    string Status);

internal static class AdobeFlashCs6Oracle
{
    private const string CompilerEnvironmentVariable =
        "SHOCKWAVEFLASH_ADOBE_FLASH_CS6_PATH";

    public static string? ResolveCompilerPath()
    {
        var configured = Environment.GetEnvironmentVariable(
            CompilerEnvironmentVariable);
        var candidates = new[]
        {
            configured,
            OperatingSystem.IsWindows()
                ? @"C:\Program Files (x86)\Adobe\Adobe Flash CS6\Flash.exe"
                : null
        };
        return candidates.FirstOrDefault(path =>
            !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    public static async Task<AdobeFlashCs6OracleResult?> TryCompileAsync(
        string source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var compilerPath = ResolveCompilerPath();
        if (compilerPath is null || IsFlashRunning())
            return null;

        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ShockwaveFlash-AdobeCs6-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "Oracle.as");
        var scriptPath = Path.Combine(directory, "Compile.jsfl");
        var documentPath = Path.Combine(directory, "Oracle.fla");
        var swfPath = Path.Combine(directory, "Oracle.swf");
        var statusPath = Path.Combine(directory, "Oracle.status");
        Process? process = null;
        try
        {
            await File.WriteAllTextAsync(
                sourcePath,
                source,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            await File.WriteAllTextAsync(
                scriptPath,
                CreateScript(sourcePath, documentPath, swfPath, statusPath),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            var startInfo = new ProcessStartInfo
            {
                FileName = compilerPath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("-execute");
            startInfo.ArgumentList.Add(scriptPath);
            process = Process.Start(startInfo) ??
                throw new InvalidOperationException(
                    "Adobe Flash CS6 did not create a compiler process.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            while (!File.Exists(statusPath))
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Delay(100, timeout.Token);
            }

            var status = await File.ReadAllTextAsync(
                statusPath,
                timeout.Token);
            if (!status.StartsWith("ok ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Adobe Flash CS6 JSFL compilation failed: " + status);
            }
            if (!File.Exists(swfPath))
            {
                throw new InvalidOperationException(
                    "Adobe Flash CS6 reported success without writing a SWF.");
            }

            var swf = await File.ReadAllBytesAsync(swfPath, timeout.Token);
            await WaitForExitAsync(process, timeout.Token);
            return new AdobeFlashCs6OracleResult(
                swf,
                compilerPath,
                status);
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: false);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            process?.Dispose();
            TryDeleteDirectory(directory);
        }
    }

    private static bool IsFlashRunning()
    {
        var processes = Process.GetProcessesByName("Flash");
        try
        {
            return processes.Length != 0;
        }
        finally
        {
            for (var i = 0; i < processes.Length; i++)
                processes[i].Dispose();
        }
    }

    private static async Task WaitForExitAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        if (!process.HasExited)
            await process.WaitForExitAsync(cancellationToken);
    }

    private static string CreateScript(
        string sourcePath,
        string documentPath,
        string swfPath,
        string statusPath) => $$"""
        var sourceUri = {{ToJsString(new Uri(sourcePath).AbsoluteUri)}};
        var flaUri = {{ToJsString(new Uri(documentPath).AbsoluteUri)}};
        var swfUri = {{ToJsString(new Uri(swfPath).AbsoluteUri)}};
        var statusUri = {{ToJsString(new Uri(statusPath).AbsoluteUri)}};
        try {
            var source = FLfile.read(sourceUri);
            var document = fl.createDocument("timeline");
            document.asVersion = 2;
            document.getTimeline().layers[0].frames[0].actionScript = source;
            var saved = fl.saveDocument(document, flaUri);
            var exported = document.exportSWF(swfUri, true);
            var compilerErrors = fl.compilerErrors;
            if (compilerErrors && compilerErrors.length) {
                FLfile.write(statusUri, "compile-error " + compilerErrors);
            } else {
                FLfile.write(statusUri, "ok saved=" + saved + " exported=" + exported);
            }
            document.close(false);
        } catch (error) {
            FLfile.write(statusUri, "error " + error);
        }
        fl.quit();
        """;

    private static string ToJsString(string value) =>
        '"' + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + '"';

    private static void TryDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 3)
            {
                Thread.Sleep(100);
            }
        }
    }
}
