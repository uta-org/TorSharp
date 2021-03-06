using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;
using Knapcode.TorSharp.PInvoke;

namespace Knapcode.TorSharp.Tools
{
    internal class SimpleToolRunner : IToolRunner, IDisposable
    {
        private static readonly Task CompletedTask = Task.FromResult(0);
        private bool _disposed;
        private readonly ConcurrentBag<Process> _processes = new ConcurrentBag<Process>();

        ~SimpleToolRunner()
        {
            Dispose(false);
        }

        public Task StartAsync(Tool tool, ITorSharpProxy proxy)
        {
            // start the desired process
            var arguments = string.Join(" ", tool.Settings.GetArguments(tool));
            var environmentVariables = tool.Settings.GetEnvironmentVariables(tool);
            var startInfo = new ProcessStartInfo
            {
                FileName = tool.ExecutablePath,
                Arguments = arguments,
                WorkingDirectory = tool.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            foreach (var pair in environmentVariables)
            {
                startInfo.EnvironmentVariables[pair.Key] = pair.Value;
            }

            var process = Process.Start(startInfo);

            var onOutput = proxy.GetHandler(true);
            if (onOutput != null)
                process.OutputDataReceived += onOutput;
            else
                process.OutputDataReceived += (sender, e) =>
                {
                    Console.WriteLine(e.Data);
                };

            var onError = proxy.GetHandler(false);
            if (onError != null)
                process.ErrorDataReceived += onError;
            else
                process.ErrorDataReceived += (sender, e) =>
                {
                    Console.Error.WriteLine(e.Data);
                };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (process == null)
            {
                throw new TorSharpException($"Unable to start the process '{tool.ExecutablePath}'.");
            }

            _processes.Add(process);

            return CompletedTask;
        }

        public void Stop()
        {
            while (!_processes.IsEmpty)
            {
                if (_processes.TryTake(out var process))
                {
                    using (process)
                    {
                        // If the process has not yet exited, ask nicely first.
                        if (!process.HasExited)
                        {
                            if (process.CloseMainWindow())
                            {
                                process.WaitForExit(1000);
                            }
                        }

                        // Still not exited? Then it's no more Mr Nice Guy.
                        if (!process.HasExited)
                        {
                            process.Kill();
                            process.WaitForExit(1000);
                        }
                    }
                }
            }
        }

        void IDisposable.Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                try
                {
                    Stop();
                }
                catch
                {
                    // Not much can be done, but must stop this bubbling.
                }
            }

            // release any unmanaged objects
            // set the object references to null
            _disposed = true;
        }
    }
}