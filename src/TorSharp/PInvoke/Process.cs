using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace Knapcode.TorSharp.PInvoke
{
    internal static partial class WindowsApi
    {
        public const uint INFINITE = 0xFFFFFFFF;
        public const uint WAIT_ABANDONED = 0x00000080;
        public const uint WAIT_TIMEOUT = 0x00000102;

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("user32.dll")]
        public static extern uint WaitForInputIdle(IntPtr hProcess, uint dwMilliseconds);

        [DllImport("kernel32.dll")]
        public static extern bool CreateProcess(
            string lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            int dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            ref PROCESS_INFORMATION lpProcessInformation);

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }
    }

    internal static partial class WindowsUtility
    {
        public static WindowsApi.PROCESS_INFORMATION CreateProcess(ITorSharpProxy proxy, ProcessStartInfo startInfo, string desktopName = null, int? millisecondsToWait = 100)
        {
            // Source: https://stackoverflow.com/a/59387440/3286975
            var startupInfo = new WindowsApi.STARTUPINFO();
            startupInfo.cb = Marshal.SizeOf(startupInfo);
            startupInfo.lpDesktop = desktopName;

            var processInformation = new WindowsApi.PROCESS_INFORMATION();

            string command = startInfo.FileName + " " + startInfo.Arguments;

            WindowsApi.SECURITY_ATTRIBUTES saAttr = new WindowsApi.SECURITY_ATTRIBUTES
            {
                nLength = (uint)Marshal.SizeOf(typeof(WindowsApi.SECURITY_ATTRIBUTES)),
                bInheritHandle = 0x1,
                lpSecurityDescriptor = IntPtr.Zero
            };

            WindowsApi.CreatePipe(ref out_read, ref out_write, ref saAttr, 0);
            WindowsApi.CreatePipe(ref err_read, ref err_write, ref saAttr, 0);

            WindowsApi.SetHandleInformation(out_read, HANDLE_FLAG_INHERIT, 0);
            WindowsApi.SetHandleInformation(err_read, HANDLE_FLAG_INHERIT, 0);

            bool result = WindowsApi.CreateProcess(null,
                command,
                IntPtr.Zero,
                IntPtr.Zero,
                true,
                WindowsApi.NORMAL_PRIORITY_CLASS,
                IntPtr.Zero,
                startInfo.WorkingDirectory,
                ref startupInfo,
                ref processInformation);

            var thread = new Thread(() => RedirectStd(proxy));
            thread.Start();

            if (result)
            {
                if (millisecondsToWait.HasValue)
                {
                    WindowsApi.WaitForInputIdle(processInformation.hProcess, (uint)millisecondsToWait.Value);
                }

                WindowsApi.CloseHandle(processInformation.hThread);
                return processInformation;
            }

            return new WindowsApi.PROCESS_INFORMATION();
        }

        private static void RedirectStd(ITorSharpProxy proxy)
        {
            // Source: https://stackoverflow.com/a/59387440/3286975

            byte[] out_buf = new byte[BUFSIZE];
            byte[] err_buf = new byte[BUFSIZE];

            int dwRead = 0;

            string out_str = "";
            string err_str = "";

            bool isOutputSet = proxy.GetHandler(true) != null;
            bool isErrorSet = proxy.GetHandler(false) != null;
            while (true)
            {
                bool bSuccess;

                if (isOutputSet)
                {
                    bSuccess = WindowsApi.ReadFile(out_read, out_buf, BUFSIZE, ref dwRead, IntPtr.Zero);
                    if (!bSuccess || dwRead == 0)
                        break;

                    out_str += System.Text.Encoding.Default.GetString(out_buf);
                    out_str = PushCallback(proxy, out_str, true);
                }

                if (isErrorSet)
                {
                    bSuccess = WindowsApi.ReadFile(out_read, err_buf, BUFSIZE, ref dwRead, IntPtr.Zero);
                    if (!bSuccess || dwRead == 0)
                        break;

                    err_str += System.Text.Encoding.Default.GetString(err_buf);
                    err_str = PushCallback(proxy, err_str, true);
                }
            }

            WindowsApi.CloseHandle(out_read);
            WindowsApi.CloseHandle(err_read);
            WindowsApi.CloseHandle(out_write);
            WindowsApi.CloseHandle(err_write);
        }

        /*
        public static WindowsApi.PROCESS_INFORMATION CreateProcess(ITorSharpProxy proxy, ProcessStartInfo startInfo, string desktopName = null, int? millisecondsToWait = 100)
        {
            var startupInfo = new WindowsApi.STARTUPINFO();
            startupInfo.cb = Marshal.SizeOf(startupInfo);
            startupInfo.lpDesktop = desktopName;

            var processInformation = new WindowsApi.PROCESS_INFORMATION();

            string command = startInfo.FileName + " " + startInfo.Arguments;

            bool result = WindowsApi.CreateProcess(null,
                command,
                IntPtr.Zero,
                IntPtr.Zero,
                true,
                WindowsApi.NORMAL_PRIORITY_CLASS,
                IntPtr.Zero,
                startInfo.WorkingDirectory,
                ref startupInfo,
                ref processInformation);

            if (result)
            {
                if (millisecondsToWait.HasValue)
                {
                    WindowsApi.WaitForInputIdle(processInformation.hProcess, (uint)millisecondsToWait.Value);
                }

                WindowsApi.CloseHandle(processInformation.hThread);
                return processInformation;
            }

            return new WindowsApi.PROCESS_INFORMATION();
        }
        */

        public static string GetLastErrorMessage()
        {
            return new Win32Exception(Marshal.GetLastWin32Error()).Message;
        }

        private static string[] Split(this string str)
        {
            return str.Split(
                new[] { "\r\n", "\r", "\n" },
                StringSplitOptions.None
            );
        }

        private static string PushCallback(ITorSharpProxy proxy, string str, bool isOutStream)
        {
            var lines = str.Split();

            foreach (var line in lines)
            {
                var instance = (DataReceivedEventArgs)Activator.CreateInstance(typeof(DataReceivedEventArgs), line);
                if (isOutStream)
                    ((TorSharpProxy)proxy).HandleOnOutput(null, instance);
                else
                    ((TorSharpProxy)proxy).HandleOnError(null, instance);
            }

            return lines.Last();
        }
    }
}