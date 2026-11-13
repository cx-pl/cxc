using System.Diagnostics;
using System.Text;

namespace CxCompiler.Generators;

public sealed record GeneratorWorkerResult(
    ulong RequestId,
    IReadOnlyList<GeneratorProtocolFrame> Messages,
    string StandardError);

public sealed class GeneratorWorkerException : Exception
{
    public GeneratorWorkerException(string message, string standardError)
        : base(standardError.Length == 0 ? message : $"{message}{Environment.NewLine}{standardError}")
    {
        StandardError = standardError;
    }

    public GeneratorWorkerException(string message, string standardError, Exception innerException)
        : base(standardError.Length == 0 ? message : $"{message}{Environment.NewLine}{standardError}", innerException)
    {
        StandardError = standardError;
    }

    public string StandardError { get; }
}

/// <summary>Runs one isolated generator request over the versioned stdio protocol.</summary>
public static class GeneratorWorkerClient
{
    private const int MaximumRequestFrames = 4096;
    private const long MaximumRequestBytes = 256L * 1024 * 1024;
    private const long MaximumResponseBytes = 256L * 1024 * 1024;
    private const int MaximumStandardErrorCharacters = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task<GeneratorWorkerResult> RunAsync(
        ProcessStartInfo startInfo,
        IReadOnlyList<GeneratorProtocolFrame> requests,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(requests);
        if (string.IsNullOrWhiteSpace(startInfo.FileName))
            throw new ArgumentException("A generator worker executable is required.", nameof(startInfo));
        if (requests.Count is 0 or > MaximumRequestFrames)
            throw new ArgumentException($"A worker request must contain 1 to {MaximumRequestFrames} frames.", nameof(requests));
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "A worker timeout must be positive.");

        var requestId = requests[0].RequestId;
        long requestBytes = 0;
        foreach (var frame in requests)
        {
            ArgumentNullException.ThrowIfNull(frame);
            if (frame.Version != GeneratorProtocol.CurrentVersion || frame.RequestId != requestId)
                throw new ArgumentException("All worker request frames must use the current protocol version and one request ID.", nameof(requests));
            if (frame.Kind is GeneratorMessageKind.SourceOutput or GeneratorMessageKind.Diagnostic or
                GeneratorMessageKind.Complete or GeneratorMessageKind.Failure)
                throw new ArgumentException($"'{frame.Kind}' is a worker response and cannot be sent as a request.", nameof(requests));
            if (!Enum.IsDefined(frame.Kind))
                throw new ArgumentException($"Unknown generator protocol message kind '{frame.Kind}'.", nameof(requests));
            if (frame.Payload.Length > GeneratorProtocol.MaximumPayloadLength)
                throw new ArgumentException("A generator request frame exceeds the protocol payload limit.", nameof(requests));
            requestBytes = checked(requestBytes + GeneratorProtocol.HeaderLength + frame.Payload.Length);
            if (requestBytes > MaximumRequestBytes)
                throw new ArgumentException("Generator request exceeds the 256 MiB aggregate limit.", nameof(requests));
        }

        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutSource.Token);
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
                throw new GeneratorWorkerException("The generator worker could not be started.", string.Empty);
        }
        catch (GeneratorWorkerException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new GeneratorWorkerException(
                $"Unable to start generator worker '{startInfo.FileName}'.", string.Empty, exception);
        }

        var standardErrorTask = DrainStandardErrorAsync(process.StandardError);
        var writeTask = WriteRequestsAsync(process, requests, linkedSource.Token);
        var messages = new List<GeneratorProtocolFrame>();
        long responseBytes = 0;
        var completed = false;
        try
        {
            while (true)
            {
                var readTask = GeneratorProtocol.ReadAsync(
                    process.StandardOutput.BaseStream, linkedSource.Token).AsTask();
                if (!writeTask.IsCompleted)
                {
                    var first = await Task.WhenAny(readTask, writeTask).ConfigureAwait(false);
                    if (first == writeTask && writeTask.IsFaulted)
                    {
                        linkedSource.Cancel();
                        await writeTask.ConfigureAwait(false);
                    }
                }
                var frame = await readTask.ConfigureAwait(false);
                if (frame is null)
                {
                    await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
                    await IgnoreFailureAsync(writeTask).ConfigureAwait(false);
                    var stderr = await standardErrorTask.ConfigureAwait(false);
                    var receivedFrames = string.Join(", ", messages.Select(message => message.Kind));
                    var progress = messages.Count == 0
                        ? "no response frames"
                        : $"response frames: {receivedFrames}";
                    throw new GeneratorWorkerException(
                        $"Generator worker exited with code {process.ExitCode} before sending completion " +
                        $"after {progress}.", stderr);
                }

                if (frame.RequestId != requestId)
                    throw new InvalidDataException("Generator worker returned a frame for a different request ID.");
                responseBytes = checked(responseBytes + GeneratorProtocol.HeaderLength + frame.Payload.Length);
                if (responseBytes > MaximumResponseBytes)
                    throw new InvalidDataException("Generator worker response exceeds the 256 MiB aggregate limit.");
                if (frame.Kind == GeneratorMessageKind.Failure)
                {
                    var stderr = await ReadStandardErrorAfterStopAsync(process, standardErrorTask)
                        .ConfigureAwait(false);
                    await IgnoreFailureAsync(writeTask).ConfigureAwait(false);
                    throw new GeneratorWorkerException(DecodeFailure(frame.Payload), stderr);
                }
                if (frame.Kind == GeneratorMessageKind.Complete)
                {
                    completed = true;
                    break;
                }
                if (frame.Kind is not (GeneratorMessageKind.Handshake or
                    GeneratorMessageKind.SourceOutput or GeneratorMessageKind.Diagnostic))
                    throw new InvalidDataException($"Unexpected generator worker response '{frame.Kind}'.");

                messages.Add(frame);
                if (messages.Count > MaximumRequestFrames)
                    throw new InvalidDataException("Generator worker returned too many response frames.");
            }

            await writeTask.ConfigureAwait(false);
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
            var standardError = await standardErrorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new GeneratorWorkerException(
                    $"Generator worker exited with code {process.ExitCode} after completion.", standardError);

            // Completion must be the last protocol frame; trailing bytes are a protocol error.
            if (await GeneratorProtocol.ReadAsync(process.StandardOutput.BaseStream, linkedSource.Token)
                    .ConfigureAwait(false) is not null)
                throw new InvalidDataException("Generator worker sent data after its completion frame.");

            return new GeneratorWorkerResult(requestId, messages.AsReadOnly(), standardError);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested &&
            timeoutSource.IsCancellationRequested)
        {
            await StopWorkerAsync(process).ConfigureAwait(false);
            await IgnoreFailureAsync(writeTask).ConfigureAwait(false);
            var stderr = await IgnoreFailureAsync(standardErrorTask).ConfigureAwait(false);
            var progress = DescribeWorkerProgress(messages);
            throw new GeneratorWorkerException(
                $"Generator worker exceeded its {timeout.TotalMilliseconds:0} ms time limit {progress}.",
                stderr, exception);
        }
        catch (OperationCanceledException)
        {
            await StopWorkerAsync(process).ConfigureAwait(false);
            await IgnoreFailureAsync(writeTask).ConfigureAwait(false);
            _ = await IgnoreFailureAsync(standardErrorTask).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException or IOException)
        {
            await StopWorkerAsync(process).ConfigureAwait(false);
            await IgnoreFailureAsync(writeTask).ConfigureAwait(false);
            var stderr = await IgnoreFailureAsync(standardErrorTask).ConfigureAwait(false);
            throw new GeneratorWorkerException("Generator worker protocol failed.", stderr, exception);
        }
        finally
        {
            if (!completed && !process.HasExited)
                await StopWorkerAsync(process).ConfigureAwait(false);
        }
    }

    private static string DescribeWorkerProgress(
        IReadOnlyList<GeneratorProtocolFrame> messages)
    {
        if (messages.Count == 0) return "before sending a response";
        var lastSourceOutput = messages.LastOrDefault(
            message => message.Kind == GeneratorMessageKind.SourceOutput);
        if (lastSourceOutput is null)
            return $"after {messages.Count} response frame(s)";
        try
        {
            var source = GeneratorWorkerMessages.DecodeSourceOutput(lastSourceOutput.Payload);
            return $"after {messages.Count} response frame(s); last source: '{source.HintName}'";
        }
        catch (InvalidDataException)
        {
            return $"after {messages.Count} response frame(s)";
        }
    }

    private static async Task WriteRequestsAsync(
        Process process,
        IReadOnlyList<GeneratorProtocolFrame> requests,
        CancellationToken cancellationToken)
    {
        var stream = process.StandardInput.BaseStream;
        foreach (var request in requests)
        {
            var bytes = GeneratorProtocol.Encode(request);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();
    }

    private static async Task<string> DrainStandardErrorAsync(StreamReader reader)
    {
        var retained = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) return retained.ToString();
            var keep = Math.Min(read, MaximumStandardErrorCharacters - retained.Length);
            if (keep > 0) retained.Append(buffer, 0, keep);
        }
    }

    private static async Task<string> ReadStandardErrorAfterStopAsync(
        Process process,
        Task<string> standardErrorTask)
    {
        await StopWorkerAsync(process).ConfigureAwait(false);
        return await IgnoreFailureAsync(standardErrorTask).ConfigureAwait(false);
    }

    private static async Task StopWorkerAsync(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        try { await process.WaitForExitAsync().ConfigureAwait(false); }
        catch (InvalidOperationException) { }
    }

    private static async Task<string> IgnoreFailureAsync(Task<string> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch (IOException) { return string.Empty; }
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (IOException) { }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private static string DecodeFailure(ReadOnlySpan<byte> payload)
    {
        try { return StrictUtf8.GetString(payload); }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Generator worker failure message is not valid UTF-8.", exception);
        }
    }
}
