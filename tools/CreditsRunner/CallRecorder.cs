// SPDX-FileCopyrightText: 2026 rlauuzo
// SPDX-License-Identifier: GPL-3.0-only

using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using IntroSkipper.FFmpeg;
using Microsoft.Extensions.Logging;

namespace CreditsRunner;

/// <summary>
/// Records the analyzer's calls into the ffmpeg service and the ffmpeg processes each one starts.
/// Pass it as the <see cref="FFmpegService"/> logger, which its process runner shares, and hand
/// the analyzer <see cref="Wrap"/> of that service. One recorder serves one pass, with calls
/// made one at a time.
/// </summary>
/// <remarks>
/// The process runner logs <c>Starting ffmpeg with the following arguments</c> at debug before
/// every start, and the recorder counts those lines. The proxy times any method of
/// <see cref="IFFmpegService"/> that returns a task, so a rewrite that renames or adds service
/// calls needs no change here.
/// </remarks>
internal sealed class CallRecorder : ILogger<FFmpegService>
{
    private readonly List<CallTiming> _calls = [];
    private int _processes;

    /// <summary>
    /// Gets the calls recorded so far, in order.
    /// </summary>
    public IReadOnlyList<CallTiming> Calls => _calls;

    /// <summary>
    /// Wraps a service so that every call through the wrapper is recorded.
    /// </summary>
    /// <param name="service">The service the analyzer should call.</param>
    /// <returns>The recording wrapper.</returns>
    public IFFmpegService Wrap(IFFmpegService service) => RecordingProxy.Create(service, this);

    /// <summary>
    /// Takes a reading to measure a span of work against.
    /// </summary>
    /// <returns>The reading.</returns>
    public Mark Begin() => new(Stopwatch.GetTimestamp(), Volatile.Read(ref _processes), ChildCpu.Seconds());

    /// <summary>
    /// Measures the work since a reading.
    /// </summary>
    /// <param name="trigger">What the work was.</param>
    /// <param name="mark">The reading from <see cref="Begin"/>.</param>
    /// <returns>The processes started, wall-clock and child CPU time since the reading.</returns>
    public CallTiming Measure(string trigger, Mark mark)
    {
        var cpu = ChildCpu.Seconds();
        return new CallTiming(trigger, Volatile.Read(ref _processes) - mark.Processes, Stopwatch.GetElapsedTime(mark.Timestamp).TotalSeconds, cpu - mark.Cpu);
    }

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    /// <inheritdoc/>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel == LogLevel.Debug && formatter(state, exception).StartsWith("Starting ffmpeg", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _processes);
        }
    }

    private void Record(string trigger, Mark mark) => _calls.Add(Measure(trigger, mark));

    /// <summary>
    /// A reading taken before a span of work.
    /// </summary>
    /// <param name="Timestamp">The stopwatch timestamp.</param>
    /// <param name="Processes">The ffmpeg processes started before it.</param>
    /// <param name="Cpu">The CPU time of reaped child processes before it, or <see langword="null"/> off Linux.</param>
    internal readonly record struct Mark(long Timestamp, int Processes, double? Cpu);

    /// <summary>
    /// Forwards every service call to the real service and records the ones that return a task
    /// once the task completes, before the caller resumes.
    /// </summary>
    internal class RecordingProxy : DispatchProxy
    {
        private static readonly MethodInfo _recordOfT = typeof(RecordingProxy)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(method => method.Name == nameof(RecordAsync) && method.IsGenericMethodDefinition);

        private IFFmpegService _target = null!;
        private CallRecorder _recorder = null!;

        /// <summary>
        /// Creates a proxy over a service.
        /// </summary>
        /// <param name="target">The real service.</param>
        /// <param name="recorder">The recorder to report to.</param>
        /// <returns>The proxy.</returns>
        public static IFFmpegService Create(IFFmpegService target, CallRecorder recorder)
        {
            var proxy = Create<IFFmpegService, RecordingProxy>();
            var self = (RecordingProxy)(object)proxy;
            self._target = target;
            self._recorder = recorder;
            return proxy;
        }

        /// <inheritdoc/>
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (!typeof(Task).IsAssignableFrom(targetMethod.ReturnType))
            {
                return Forward(targetMethod, args);
            }

            var mark = _recorder.Begin();
            var task = (Task)Forward(targetMethod, args)!;
            return targetMethod.ReturnType.IsGenericType
                ? _recordOfT.MakeGenericMethod(targetMethod.ReturnType.GetGenericArguments()[0]).Invoke(this, [task, targetMethod.Name, mark])
                : RecordAsync(task, targetMethod.Name, mark);
        }

        private object? Forward(MethodInfo method, object?[]? args)
        {
            try
            {
                return method.Invoke(_target, args);
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                ExceptionDispatchInfo.Throw(e.InnerException);
                throw;
            }
        }

        private async Task RecordAsync(Task task, string trigger, Mark mark)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            finally
            {
                _recorder.Record(trigger, mark);
            }
        }

        private async Task<T> RecordAsync<T>(Task<T> task, string trigger, Mark mark)
        {
            try
            {
                return await task.ConfigureAwait(false);
            }
            finally
            {
                _recorder.Record(trigger, mark);
            }
        }
    }

    /// <summary>
    /// Reads the CPU time of this process's reaped children from <c>getrusage(RUSAGE_CHILDREN)</c>.
    /// .NET reaps each child when it exits, so a process a call waited for is counted by the time
    /// the call returns. Other platforms have no such counter, so the runner reports wall-clock only.
    /// </summary>
    private static class ChildCpu
    {
        private const int RusageChildren = -1;

        /// <summary>
        /// Gets the user plus system seconds of every reaped child so far.
        /// </summary>
        /// <returns>The seconds, or <see langword="null"/> off Linux or when the call fails.</returns>
        public static double? Seconds()
        {
            if (!OperatingSystem.IsLinux() || getrusage(RusageChildren, out var usage) != 0)
            {
                return null;
            }

            return usage.UserSeconds + (usage.UserMicroseconds / 1e6) + usage.SystemSeconds + (usage.SystemMicroseconds / 1e6);
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int getrusage(int who, out Rusage usage);

        // struct rusage on 64-bit Linux: two timevals, then fourteen longs the runner does not read.
        [StructLayout(LayoutKind.Sequential, Size = 144)]
        private struct Rusage
        {
            public long UserSeconds;
            public long UserMicroseconds;
            public long SystemSeconds;
            public long SystemMicroseconds;
        }
    }
}
