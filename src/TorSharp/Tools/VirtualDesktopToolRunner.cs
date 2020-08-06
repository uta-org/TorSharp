using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Knapcode.TorSharp.PInvoke;

namespace Knapcode.TorSharp.Tools
{
    internal class VirtualDesktopToolRunner : IToolRunner
    {
        private const string DesktopName = "TorSharpDesktop";
        private readonly object _jobHandleLock = new object();
        private IntPtr _jobHandle;
        private readonly object _processIdsLock = new object();
        private readonly HashSet<int> _processIds = new HashSet<int>();

        public Task StartAsync(Tool tool, ITorSharpProxy proxy)
        {
            IntPtr desktopHandle = WindowsUtility.CreateDesktop(DesktopName);

            // embrace the madness -- it seems Windows always wants exactly one instance of "ctfmon.exe" in the new desktop
            var ctfmonStartInfo = new ProcessStartInfo { FileName = "ctfmon.exe", WorkingDirectory = "." };
            WindowsApi.PROCESS_INFORMATION ctfmonProcess = WindowsUtility.CreateProcess(ctfmonStartInfo, DesktopName);
            AssociateWithJob(ctfmonProcess, false);

            // start the desired process
            var arguments = string.Join(" ", tool.Settings.GetArguments(tool));
            var startInfo = new ProcessStartInfo
            {
                FileName = tool.ExecutablePath,
                Arguments = arguments,
                WorkingDirectory = tool.WorkingDirectory
            };

            // TODO: Implement OutputDataReceived and ErrorDataReceived for WindowsApi.PROCESS_INFORMATION

            /*
             *
                if (proxy.GetType().GetField("OnOutput", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(proxy) is
                    DataReceivedEventHandler onOutput)
                    process.OutputDataReceived += onOutput;
                else
                    process.OutputDataReceived += (sender, e) =>
                    {
                        Console.WriteLine(e.Data);
                    };

                if (proxy.GetType().GetField("OnError", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(proxy) is
                    DataReceivedEventHandler onError)
                    process.ErrorDataReceived += onError;
                else
                    process.ErrorDataReceived += (sender, e) =>
                    {
                        Console.Error.WriteLine(e.Data);
                    };
             *
             */

            WindowsApi.PROCESS_INFORMATION targetProcess = WindowsUtility.CreateProcess(startInfo, DesktopName);
            AssociateWithJob(targetProcess, true);

            return Task.FromResult((object)null);
        }

        private void AssociateWithJob(WindowsApi.PROCESS_INFORMATION processInformation, bool throwOnError)
        {
            if (_jobHandle == IntPtr.Zero)
            {
                lock (_jobHandleLock)
                {
                    if (_jobHandle == IntPtr.Zero)
                    {
                        _jobHandle = WindowsApi.CreateJobObject(IntPtr.Zero, null);

                        var basic = new WindowsApi.JOBOBJECT_BASIC_LIMIT_INFORMATION { LimitFlags = WindowsApi.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE };
                        var extended = new WindowsApi.JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = basic };

                        int length = Marshal.SizeOf(typeof(WindowsApi.JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                        IntPtr extendedPointer = Marshal.AllocHGlobal(length);
                        Marshal.StructureToPtr(extended, extendedPointer, false);

                        if (!WindowsApi.SetInformationJobObject(_jobHandle, WindowsApi.JOBOBJECTINFOCLASS.ExtendedLimitInformation, extendedPointer, (uint)length))
                        {
                            throw new TorSharpException($"Unable to set information on the job object. Error: {WindowsUtility.GetLastErrorMessage()}.");
                        }
                    }
                }
            }

            if (processInformation.dwProcessId != 0)
            {
                lock (_processIdsLock)
                {
                    _processIds.Add(processInformation.dwProcessId);
                }
            }

            if (!WindowsApi.AssignProcessToJobObject(_jobHandle, processInformation.hProcess) && throwOnError)
            {
                throw new TorSharpException($"Unable to assign the process to the job object. Error: {WindowsUtility.GetLastErrorMessage()}.");
            }
        }

        public void Stop()
        {
            if (_jobHandle != IntPtr.Zero)
            {
                lock (_jobHandleLock)
                {
                    if (_jobHandle != IntPtr.Zero)
                    {
                        // terminate the job
                        if (!WindowsApi.TerminateJobObject(_jobHandle, 0))
                        {
                            throw new TorSharpException($"Unable to terminate the job object. Error: {WindowsUtility.GetLastErrorMessage()}.");
                        }

                        // wait for all jobs to complete
                        lock (_processIdsLock)
                        {
                            var firstAttempt = true;
                            while (_processIds.Any())
                            {
                                if (!firstAttempt)
                                {
                                    Thread.Sleep(100);
                                }

                                firstAttempt = false;

                                var allProcessIds = Process
                                    .GetProcesses()
                                    .Select(x => x.Id)
                                    .ToList();

                                _processIds.IntersectWith(allProcessIds);
                            }
                        }

                        _jobHandle = IntPtr.Zero;
                    }
                }
            }
        }
    }
}