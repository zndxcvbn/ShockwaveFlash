using System.Diagnostics;
using System.Text;

namespace ShockwaveFlash.Tests.Framework;

internal sealed record MtascOracleResult(
    byte[] Swf,
    string CompilerPath,
    string StandardOutput,
    string StandardError);

internal static class MtascOracle
{
    private const string CompilerEnvironmentVariable =
        "SHOCKWAVEFLASH_MTASC_PATH";

    public static string? ResolveCompilerPath()
    {
        var configured = Environment.GetEnvironmentVariable(
            CompilerEnvironmentVariable);
        var candidates = new[]
        {
            configured,
            OperatingSystem.IsWindows()
                ? @"C:\Program Files\FlashDevelop\Tools\mtasc\mtasc.exe"
                : null
        };
        return candidates.FirstOrDefault(path =>
            !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    public static async Task<MtascOracleResult?> TryCompileAsync(
        string source,
        string sourceFileName,
        byte swfVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFileName);
        if (Path.GetFileName(sourceFileName) != sourceFileName)
        {
            throw new ArgumentException(
                "The MTASC source name must not contain a directory.",
                nameof(sourceFileName));
        }

        var compilerPath = ResolveCompilerPath();
        if (compilerPath is null)
            return null;

        var directory = Path.Combine(
            Path.GetTempPath(),
            $"ShockwaveFlash-Mtasc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, sourceFileName);
        var swfPath = Path.Combine(
            directory,
            Path.GetFileNameWithoutExtension(sourceFileName) + ".swf");
        Process? process = null;
        try
        {
            await File.WriteAllTextAsync(
                sourcePath,
                source,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            var startInfo = new ProcessStartInfo
            {
                FileName = compilerPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("-version");
            startInfo.ArgumentList.Add(
                swfVersion.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-swf");
            startInfo.ArgumentList.Add(swfPath);
            startInfo.ArgumentList.Add("-header");
            startInfo.ArgumentList.Add("100:100:12");
            startInfo.ArgumentList.Add("-main");
            startInfo.ArgumentList.Add(sourcePath);

            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "MTASC did not create a compiler process.");
            }
            var stdout = process.StandardOutput.ReadToEndAsync(
                cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(
                cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            var standardOutput = await stdout;
            var standardError = await stderr;
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"MTASC exited with code {process.ExitCode}." +
                    Environment.NewLine + standardOutput +
                    Environment.NewLine + standardError);
            }
            if (!File.Exists(swfPath))
            {
                throw new InvalidOperationException(
                    "MTASC reported success without writing a SWF." +
                    Environment.NewLine + standardOutput +
                    Environment.NewLine + standardError);
            }

            return new MtascOracleResult(
                await File.ReadAllBytesAsync(swfPath, cancellationToken),
                compilerPath,
                standardOutput,
                standardError);
        }
        finally
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            process?.Dispose();
            TryDeleteDirectory(directory);
        }
    }

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
