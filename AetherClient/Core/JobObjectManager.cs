using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AetherClient.Core
{
    public sealed class JobObjectManager : IDisposable
    {
        private IntPtr _jobHandle;
        private bool _disposed;

        public JobObjectManager()
        {
            _jobHandle = NativeMethods.CreateJobObject(IntPtr.Zero, $"AetherClientJob_{Environment.ProcessId}");
            if (_jobHandle == IntPtr.Zero)
                return;

            var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };

            int length = Marshal.SizeOf(typeof(NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr ptr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                NativeMethods.SetInformationJobObject(
                    _jobHandle,
                    NativeMethods.JobObjectInfoType.ExtendedLimitInformation,
                    ptr,
                    (uint)length);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        public void AssignProcess(Process process)
        {
            if (_disposed || _jobHandle == IntPtr.Zero || process.HasExited)
                return;

            try
            {
                NativeMethods.AssignProcessToJobObject(_jobHandle, process.Handle);
            }
            catch
            {
                // Ignore if process already exited or access denied
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_jobHandle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(_jobHandle);
                _jobHandle = IntPtr.Zero;
            }
        }
    }
}
