// SPDX-FileCopyrightText: 2022 ConfusedPolarBear
// SPDX-FileCopyrightText: 2022 nyanmisaka
// SPDX-FileCopyrightText: 2024-2026 Kilian von Pflugk
// SPDX-FileCopyrightText: 2024-2026 rlauuzo
// SPDX-FileCopyrightText: 2024-2026 AbandonedCart
// SPDX-License-Identifier: GPL-3.0-only

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace IntroSkipper.FFmpeg;

/// <summary>
/// Runs FFmpeg processes with current process execution semantics:
/// captures one selected stream (stdout or stderr), drains redirected streams,
/// kills the process on timeout or cancellation, and omits <c>-threads</c> for info queries.
/// </summary>
public sealed partial class FFmpegRunner : IFFmpegRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaximumDelayTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);
    private static readonly TimeSpan PostExitStreamCloseTimeout = TimeSpan.FromMilliseconds(250);

    private readonly IPluginOptionsProvider _options;
    private readonly ILogger<FFmpegRunner> _logger;
    private readonly Func<ProcessStartInfo, IProcess> _processFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="FFmpegRunner"/> class.
    /// </summary>
    /// <param name="options">Options provider for FFmpeg path and process configuration.</param>
    /// <param name="logger">Logger instance.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="logger"/> is <see langword="null"/>.</exception>
    public FFmpegRunner(IPluginOptionsProvider options, ILogger<FFmpegRunner> logger)
        : this(options, logger, static startInfo => new SystemProcess(startInfo))
    {
    }

    internal FFmpegRunner(
        IPluginOptionsProvider options,
        ILogger<FFmpegRunner> logger,
        Func<ProcessStartInfo, IProcess> processFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(processFactory);

        _options = options;
        _logger = logger;
        _processFactory = processFactory;
    }

    internal interface IProcess : IDisposable
    {
        ProcessStartInfo StartInfo { get; }

        Stream StandardOutput { get; }

        Stream StandardError { get; }

        bool HasExited { get; }

        int ExitCode { get; }

        ProcessPriorityClass PriorityClass { set; }

        void Start();

        Task WaitForExitAsync(CancellationToken cancellationToken = default);

        void Kill(bool entireProcessTree);
    }

    /// <inheritdoc />
    public Task<FFmpegProcessResult> RunAsync(
        IReadOnlyList<string> args,
        FFmpegOutputStream outputStream = FFmpegOutputStream.Stdout,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => RunProcessAsync(_options.FFmpegPath, args, true, outputStream, timeout, cancellationToken);

    /// <inheritdoc />
    public Task<FFmpegProcessResult> RunFFprobeAsync(
        IReadOnlyList<string> args,
        FFmpegOutputStream outputStream = FFmpegOutputStream.Stdout,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        => RunProcessAsync(GetFFprobePath(), args, false, outputStream, timeout, cancellationToken);

    private async Task<FFmpegProcessResult> RunProcessAsync(
        string executablePath,
        IReadOnlyList<string> args,
        bool useFfmpegDefaults,
        FFmpegOutputStream outputStream,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var effectiveTimeout = timeout ?? DefaultTimeout;
        ValidateTimeout(timeout, effectiveTimeout);

        cancellationToken.ThrowIfCancellationRequested();

        var stderr = outputStream == FFmpegOutputStream.Stderr;
        using var ffmpeg = StartProcess(executablePath, args, useFfmpegDefaults);

        // Read the selected stream asynchronously while draining the other to prevent deadlocks.
        using var ms = new MemoryStream();
        var selectedStream = stderr ? ffmpeg.StandardError : ffmpeg.StandardOutput;
        var drainedStream = stderr ? ffmpeg.StandardOutput : ffmpeg.StandardError;

        Task<byte[]>? drainTask = null;
        try
        {
            using var selectedReadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var drainReadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var exitTask = WaitForExitAsync(ffmpeg, effectiveTimeout, cancellationToken);
            drainTask = DrainStreamAsync(drainedStream, drainReadCts.Token);

            var selectedStreamCompleted = await CopySelectedStreamAsync(selectedStream, ms, exitTask, selectedReadCts, cancellationToken).ConfigureAwait(false);
            if (!selectedStreamCompleted)
            {
                return await ReturnTimeoutAsync().ConfigureAwait(false);
            }

            var completedTask = await Task.WhenAny(drainTask, exitTask).ConfigureAwait(false);
            if (completedTask == exitTask)
            {
                var exited = await exitTask.ConfigureAwait(false);
                if (!exited)
                {
                    return await ReturnTimeoutAsync().ConfigureAwait(false);
                }
            }

            var drainedOutput = completedTask == drainTask
                ? await drainTask.ConfigureAwait(false)
                : await CompleteDrainAfterExitAsync(drainTask, drainReadCts, cancellationToken).ConfigureAwait(false);
            var processExited = completedTask == exitTask || await exitTask.ConfigureAwait(false);
            if (!processExited)
            {
                await KillProcessAsync(ffmpeg).ConfigureAwait(false);
            }

            var output = ms.ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            return processExited
                ? new FFmpegProcessResult(output, drainedOutput, FFmpegProcessStatus.Completed, ffmpeg.ExitCode)
                : new FFmpegProcessResult(output, Array.Empty<byte>(), FFmpegProcessStatus.TimedOut, null);

            async Task<FFmpegProcessResult> ReturnTimeoutAsync()
            {
                await KillProcessAsync(ffmpeg).ConfigureAwait(false);
                await selectedReadCts.CancelAsync().ConfigureAwait(false);
                await drainReadCts.CancelAsync().ConfigureAwait(false);
                ObserveFaultedTask(drainTask);
                var output = ms.ToArray();
                cancellationToken.ThrowIfCancellationRequested();
                return new FFmpegProcessResult(output, Array.Empty<byte>(), FFmpegProcessStatus.TimedOut, null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await KillProcessAsync(ffmpeg).ConfigureAwait(false);
            if (drainTask is not null)
            {
                ObserveFaultedTask(drainTask);
            }

            throw;
        }
    }

    private static void ValidateTimeout(TimeSpan? timeout, TimeSpan effectiveTimeout)
    {
        if (effectiveTimeout < TimeSpan.Zero && effectiveTimeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be null, Timeout.InfiniteTimeSpan, or a non-negative TimeSpan.");
        }

        if (effectiveTimeout != Timeout.InfiniteTimeSpan && effectiveTimeout > MaximumDelayTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, $"Timeout must be less than or equal to {MaximumDelayTimeout}.");
        }
    }

    /// <summary>
    /// Copies selected stream output until the stream closes or the process wait times out.
    /// </summary>
    /// <param name="source">The stream to copy from.</param>
    /// <param name="destination">The stream to copy into.</param>
    /// <param name="exitTask">The process exit wait task.</param>
    /// <param name="readCts">Cancellation source for the selected stream read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the source stream closed before timeout; otherwise <c>false</c>.</returns>
    private static async Task<bool> CopySelectedStreamAsync(
        Stream source,
        Stream destination,
        Task<bool> exitTask,
        CancellationTokenSource readCts,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (true)
        {
            var readTask = source.ReadAsync(buffer, readCts.Token).AsTask();
            var completedTask = await Task.WhenAny(readTask, exitTask).ConfigureAwait(false);
            int bytesRead;
            if (completedTask == exitTask)
            {
                var exited = await exitTask.ConfigureAwait(false);
                if (!exited)
                {
                    ObserveFaultedTask(readTask);
                    return false;
                }

                var postExitRead = await CompleteReadAfterExitAsync(readTask, readCts, cancellationToken).ConfigureAwait(false);
                if (!postExitRead.HasValue)
                {
                    return true;
                }

                bytesRead = postExitRead.Value;
            }
            else
            {
                bytesRead = await readTask.ConfigureAwait(false);
            }

            if (bytesRead == 0)
            {
                return true;
            }

            await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);

            if (exitTask.IsCompleted)
            {
                var exited = await exitTask.ConfigureAwait(false);
                if (!exited)
                {
                    return false;
                }
            }
        }
    }

    private static async Task<int?> CompleteReadAfterExitAsync(Task<int> readTask, CancellationTokenSource readCts, CancellationToken cancellationToken)
    {
        if (readTask.IsCompleted)
        {
            return await readTask.ConfigureAwait(false);
        }

        var completedTask = await Task.WhenAny(readTask, Task.Delay(PostExitStreamCloseTimeout, cancellationToken)).ConfigureAwait(false);
        if (completedTask == readTask)
        {
            return await readTask.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await readCts.CancelAsync().ConfigureAwait(false);
        ObserveFaultedTask(readTask);
        return null;
    }

    private static async Task<byte[]> CompleteDrainAfterExitAsync(
        Task<byte[]> drainTask,
        CancellationTokenSource drainCts,
        CancellationToken cancellationToken)
    {
        if (drainTask.IsCompleted)
        {
            return await drainTask.ConfigureAwait(false);
        }

        var completedTask = await Task.WhenAny(drainTask, Task.Delay(PostExitStreamCloseTimeout, cancellationToken)).ConfigureAwait(false);
        if (completedTask == drainTask)
        {
            return await drainTask.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await drainCts.CancelAsync().ConfigureAwait(false);
        ObserveFaultedTask(drainTask);
        return [];
    }

    /// <summary>
    /// Creates a configured FFmpeg process start info without starting it.
    /// </summary>
    /// <param name="args">User-supplied FFmpeg arguments.</param>
    /// <returns>A configured but not-yet-started <see cref="ProcessStartInfo"/>.</returns>
    internal ProcessStartInfo CreateProcessStartInfo(IReadOnlyList<string> args)
        => CreateProcessStartInfo(_options.FFmpegPath, args, useFfmpegDefaults: true);

    private ProcessStartInfo CreateProcessStartInfo(string executablePath, IReadOnlyList<string> args, bool useFfmpegDefaults)
    {
        var info = new ProcessStartInfo(executablePath)
        {
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false,
            ErrorDialog = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (useFfmpegDefaults)
        {
            // The silencedetect and blackframe filters output data at the info log level.
            var useInfoLevel = args.Any(a =>
                a.Contains("silencedetect", StringComparison.OrdinalIgnoreCase) ||
                a.Contains("blackframe", StringComparison.OrdinalIgnoreCase) ||
                a.Contains("showinfo", StringComparison.OrdinalIgnoreCase));
            var logLevel = useInfoLevel ? "info" : "warning";

            // For FFmpeg info queries (-version, -muxers, -h), don't add the thread count flag
            // to avoid "Trailing option(s) found" warning. These are quick queries.
            var firstArg = args.Count > 0 ? args[0] : string.Empty;
            var isInfoQuery = firstArg.StartsWith("-version", StringComparison.Ordinal) ||
                firstArg.StartsWith("-muxers", StringComparison.Ordinal) ||
                firstArg.StartsWith("-h", StringComparison.Ordinal);

            // Prepend flags to suppress FFmpeg banner and set log level / thread count.
            info.ArgumentList.Add("-hide_banner");
            if (!isInfoQuery)
            {
                info.ArgumentList.Add("-threads");
                info.ArgumentList.Add(_options.ProcessThreads.ToString(CultureInfo.InvariantCulture));
            }

            info.ArgumentList.Add("-loglevel");
            info.ArgumentList.Add(logLevel);
        }

        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        return info;
    }

    private string GetFFprobePath()
    {
        if (string.IsNullOrWhiteSpace(_options.FFmpegPath))
        {
            return "ffprobe";
        }

        var extension = Path.GetExtension(_options.FFmpegPath);
        var withoutExtension = Path.ChangeExtension(_options.FFmpegPath, null);
        var candidate = withoutExtension + "probe" + extension;
        if (File.Exists(candidate))
        {
            return candidate;
        }

        return Path.Join(Path.GetDirectoryName(_options.FFmpegPath) ?? string.Empty, "ffprobe" + extension);
    }

    /// <summary>
    /// Creates a configured FFmpeg/FFprobe process without starting it.
    /// </summary>
    /// <param name="executablePath">Executable path.</param>
    /// <param name="args">User-supplied tool arguments.</param>
    /// <param name="useFfmpegDefaults">Whether to prepend FFmpeg-specific default arguments.</param>
    /// <returns>A configured but not-yet-started process.</returns>
    private IProcess CreateProcess(string executablePath, IReadOnlyList<string> args, bool useFfmpegDefaults)
        => _processFactory(CreateProcessStartInfo(executablePath, args, useFfmpegDefaults));

    /// <summary>
    /// Creates, logs, starts, and applies configured priority to an FFmpeg/FFprobe process.
    /// </summary>
    /// <param name="executablePath">Executable path.</param>
    /// <param name="args">User-supplied tool arguments.</param>
    /// <param name="useFfmpegDefaults">Whether to prepend FFmpeg-specific default arguments.</param>
    /// <returns>A started process.</returns>
    private IProcess StartProcess(string executablePath, IReadOnlyList<string> args, bool useFfmpegDefaults)
    {
        var ffmpeg = CreateProcess(executablePath, args, useFfmpegDefaults);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            LogStartingFfmpeg(_logger, string.Join(" ", ffmpeg.StartInfo.ArgumentList));
        }

        try
        {
            ffmpeg.Start();
        }
        catch
        {
            ffmpeg.Dispose();
            throw;
        }

        SetProcessPriority(ffmpeg);
        return ffmpeg;
    }

    /// <summary>
    /// Sets the process priority, logging a warning on failure.
    /// </summary>
    /// <param name="process">The process whose priority to set.</param>
    private void SetProcessPriority(IProcess process)
    {
        try
        {
            process.PriorityClass = _options.ProcessPriority;
        }
        catch (Exception e) when (e is InvalidOperationException or
            Win32Exception or
            NotSupportedException)
        {
            LogFfmpegPriorityNotModified(_logger, e.Message);
        }
    }

    /// <summary>
    /// Waits for process exit and reports whether the process exited before the timeout.
    /// </summary>
    /// <param name="process">Process to wait for.</param>
    /// <param name="timeout">Timeout duration, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> if the process exited before the timeout; otherwise <c>false</c>.</returns>
    private static async Task<bool> WaitForExitAsync(IProcess process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        var waitTask = process.WaitForExitAsync(cancellationToken);
        var timeoutTask = Task.Delay(timeout, cancellationToken);
        var completedTask = await Task.WhenAny(waitTask, timeoutTask).ConfigureAwait(false);
        if (completedTask == waitTask)
        {
            await waitTask.ConfigureAwait(false);
            return true;
        }

        await timeoutTask.ConfigureAwait(false);
        ObserveFaultedTask(waitTask);
        return false;
    }

    /// <summary>
    /// Observes a background stream task if it faults after a timeout return.
    /// </summary>
    /// <param name="task">The task whose exception should be observed.</param>
    private static void ObserveFaultedTask(Task task)
        => _ = task.ContinueWith(
            static completedTask => _ = completedTask.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// Reads all bytes from a stream to prevent pipe buffer deadlocks, capturing them for failure diagnostics.
    /// </summary>
    /// <param name="stream">Stream to drain.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The captured bytes from the drained stream.</returns>
    private static async Task<byte[]> DrainStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await ms.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Attempts to kill an FFmpeg process and its child process tree.
    /// </summary>
    /// <param name="process">The FFmpeg process to kill.</param>
    /// <returns>A task that completes when the process has been killed.</returns>
    private async Task KillProcessAsync(IProcess process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);

                // Bound the post-kill wait so a stuck process cannot hang the timeout path itself.
                using var killCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(killCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (killCts.IsCancellationRequested)
                {
                    LogFfmpegKillFailed(_logger, "Process did not exit within 5 seconds after Kill()");
                }

                LogFfmpegProcessKilled(_logger);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or
            Win32Exception or
            NotSupportedException)
        {
            LogFfmpegKillFailed(_logger, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Starting ffmpeg with the following arguments: {Arguments}")]
    private static partial void LogStartingFfmpeg(ILogger logger, string arguments);

    [LoggerMessage(Level = LogLevel.Debug, Message = "ffmpeg priority could not be modified. {Message}")]
    private static partial void LogFfmpegPriorityNotModified(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Debug, Message = "FFmpeg process killed after timeout or cancellation")]
    private static partial void LogFfmpegProcessKilled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to kill FFmpeg process: {Message}")]
    private static partial void LogFfmpegKillFailed(ILogger logger, string message);

    private sealed class SystemProcess : IProcess
    {
        private readonly Process _process;

        internal SystemProcess(ProcessStartInfo startInfo)
        {
            _process = new Process { StartInfo = startInfo };
        }

        public ProcessStartInfo StartInfo => _process.StartInfo;

        public Stream StandardOutput => _process.StandardOutput.BaseStream;

        public Stream StandardError => _process.StandardError.BaseStream;

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.ExitCode;

        public ProcessPriorityClass PriorityClass
        {
            set => _process.PriorityClass = value;
        }

        public void Start() => _process.Start();

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) => _process.WaitForExitAsync(cancellationToken);

        public void Kill(bool entireProcessTree) => _process.Kill(entireProcessTree);

        public void Dispose() => _process.Dispose();
    }
}
