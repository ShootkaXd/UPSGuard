using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UPSGuard.Service;

public sealed class UserSessionLauncher
{
    private readonly ServiceLogger _log;

    public UserSessionLauncher(ServiceLogger log)
    {
        _log = log;
    }

    public IReadOnlyList<int> GetActiveUserSessions()
    {
        var result = new List<int>();

        try
        {
            if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var ppSessionInfo, out var count))
            {
                var err = Marshal.GetLastWin32Error();
                _log.Warn(nameof(UserSessionLauncher), $"WTSEnumerateSessions failed. Win32={err}, Message={new Win32Exception(err).Message}");
                return result;
            }

            try
            {
                var dataSize = Marshal.SizeOf<WTS_SESSION_INFO>();
                var current = ppSessionInfo;

                for (var i = 0; i < count; i++)
                {
                    var sessionInfo = Marshal.PtrToStructure<WTS_SESSION_INFO>(current);

                    if (sessionInfo.State == WTS_CONNECTSTATE_CLASS.WTSActive)
                    {
                        if (TryGetUserName(sessionInfo.SessionID, out var userName) &&
                            !string.IsNullOrWhiteSpace(userName))
                        {
                            result.Add(unchecked((int)sessionInfo.SessionID));
                        }
                    }

                    current = IntPtr.Add(current, dataSize);
                }
            }
            finally
            {
                WTSFreeMemory(ppSessionInfo);
            }
        }
        catch (Exception ex)
        {
            _log.Error(nameof(UserSessionLauncher), "GetActiveUserSessions failed", ex);
        }

        return result.Distinct().OrderBy(x => x).ToList();
    }

    public bool HasUnlockedActiveUserSession()
    {
        try
        {
            var sessions = GetActiveUserSessions();

            foreach (var sessionId in sessions)
            {
                if (IsSessionUnlocked((uint)sessionId))
                {
                    _log.Info(nameof(UserSessionLauncher), $"Unlocked active session found. Session={sessionId}");
                    return true;
                }
            }

            _log.Warn(nameof(UserSessionLauncher), "No unlocked active user sessions found.");
            return false;
        }
        catch (Exception ex)
        {
            _log.Error(nameof(UserSessionLauncher), "HasUnlockedActiveUserSession failed", ex);
            return false;
        }
    }

    private bool IsSessionUnlocked(uint sessionId)
    {
        IntPtr buffer = IntPtr.Zero;

        try
        {
            if (!WTSQuerySessionInformation(
                    IntPtr.Zero,
                    sessionId,
                    WTS_INFO_CLASS.WTSSessionInfoEx,
                    out buffer,
                    out var bytesReturned))
            {
                var err = Marshal.GetLastWin32Error();
                _log.Warn(nameof(UserSessionLauncher),
                    $"WTSSessionInfoEx failed. Session={sessionId}, Win32={err}, Message={new Win32Exception(err).Message}");
                return false;
            }

            if (buffer == IntPtr.Zero || bytesReturned < Marshal.SizeOf<WTSINFOEX>())
                return false;

            var info = Marshal.PtrToStructure<WTSINFOEX>(buffer);

            return info.Data.SessionFlags == WTS_SESSIONSTATE_UNLOCK;
        }
        catch (Exception ex)
        {
            _log.Error(nameof(UserSessionLauncher), $"IsSessionUnlocked failed. Session={sessionId}", ex);
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                WTSFreeMemory(buffer);
        }
    }

    public bool IsProcessRunningInSession(string processNameWithoutExe, int sessionId)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName(processNameWithoutExe))
            {
                try
                {
                    if (p.SessionId == sessionId && !p.HasExited)
                        return true;
                }
                catch
                {
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error(nameof(UserSessionLauncher), "Failed to enumerate processes", ex);
        }

        return false;
    }

    public bool TryLaunchInSession(string exePath, string? arguments, string workingDirectory, int sessionId, out int pid)
    {
        pid = 0;

        if (sessionId < 0)
        {
            _log.Warn(nameof(UserSessionLauncher), "Invalid session id.");
            return false;
        }

        if (!File.Exists(exePath))
        {
            _log.Warn(nameof(UserSessionLauncher), $"File not found: {exePath}");
            return false;
        }

        if (!WTSQueryUserToken((uint)sessionId, out var userToken))
        {
            var err = Marshal.GetLastWin32Error();
            _log.Error(nameof(UserSessionLauncher),
                $"WTSQueryUserToken failed. Session={sessionId}, Win32={err}, Message={new Win32Exception(err).Message}");
            return false;
        }

        using (userToken)
        {
            if (!DuplicateTokenEx(
                    userToken,
                    TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID,
                    IntPtr.Zero,
                    SECURITY_IMPERSONATION_LEVEL.SecurityImpersonation,
                    TOKEN_TYPE.TokenPrimary,
                    out var primaryToken))
            {
                var err = Marshal.GetLastWin32Error();
                _log.Error(nameof(UserSessionLauncher),
                    $"DuplicateTokenEx failed. Session={sessionId}, Win32={err}, Message={new Win32Exception(err).Message}");
                return false;
            }

            using (primaryToken)
            {
                IntPtr environment = IntPtr.Zero;

                try
                {
                    if (!CreateEnvironmentBlock(out environment, primaryToken, false))
                    {
                        var err = Marshal.GetLastWin32Error();
                        _log.Warn(nameof(UserSessionLauncher),
                            $"CreateEnvironmentBlock failed. Session={sessionId}, Win32={err}, Message={new Win32Exception(err).Message}");
                        environment = IntPtr.Zero;
                    }

                    var startupInfo = new STARTUPINFO
                    {
                        cb = Marshal.SizeOf<STARTUPINFO>(),
                        lpDesktop = @"winsta0\default"
                    };

                    var processInfo = new PROCESS_INFORMATION();
                    var cmdLine = BuildCommandLine(exePath, arguments);

                    const uint creationFlags =
                        CREATE_UNICODE_ENVIRONMENT |
                        CREATE_NEW_PROCESS_GROUP |
                        NORMAL_PRIORITY_CLASS;

                    var ok = CreateProcessAsUser(
                        primaryToken,
                        null,
                        cmdLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        creationFlags,
                        environment,
                        workingDirectory,
                        ref startupInfo,
                        out processInfo);

                    if (!ok)
                    {
                        var err = Marshal.GetLastWin32Error();
                        _log.Error(nameof(UserSessionLauncher),
                            $"CreateProcessAsUser failed. Session={sessionId}, Win32={err}, Message={new Win32Exception(err).Message}, Cmd={cmdLine}, WorkDir={workingDirectory}");
                        return false;
                    }

                    pid = processInfo.dwProcessId;

                    _log.Info(nameof(UserSessionLauncher),
                        $"Process launched. Session={sessionId}, PID={pid}, Path={exePath}");

                    if (processInfo.hThread != IntPtr.Zero) CloseHandle(processInfo.hThread);
                    if (processInfo.hProcess != IntPtr.Zero) CloseHandle(processInfo.hProcess);

                    return true;
                }
                finally
                {
                    if (environment != IntPtr.Zero)
                        DestroyEnvironmentBlock(environment);
                }
            }
        }
    }

    private bool TryGetUserName(uint sessionId, out string? userName)
    {
        userName = null;
        IntPtr buffer = IntPtr.Zero;

        try
        {
            if (!WTSQuerySessionInformation(
                    IntPtr.Zero,
                    sessionId,
                    WTS_INFO_CLASS.WTSUserName,
                    out buffer,
                    out var bytesReturned))
            {
                return false;
            }

            if (buffer == IntPtr.Zero || bytesReturned <= 1)
                return false;

            userName = Marshal.PtrToStringUni(buffer);
            return !string.IsNullOrWhiteSpace(userName);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                WTSFreeMemory(buffer);
        }
    }

    private static string BuildCommandLine(string exePath, string? arguments)
    {
        return string.IsNullOrWhiteSpace(arguments)
            ? $"\"{exePath}\""
            : $"\"{exePath}\" {arguments}";
    }

    private const int WTS_SESSIONSTATE_LOCK = 0;
    private const int WTS_SESSIONSTATE_UNLOCK = 1;

    private const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const uint TOKEN_DUPLICATE = 0x0002;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    private const uint TOKEN_ADJUST_SESSIONID = 0x0100;

    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    private const uint NORMAL_PRIORITY_CLASS = 0x00000020;

    private enum TOKEN_TYPE
    {
        TokenPrimary = 1,
        TokenImpersonation = 2
    }

    private enum SECURITY_IMPERSONATION_LEVEL
    {
        SecurityAnonymous,
        SecurityIdentification,
        SecurityImpersonation,
        SecurityDelegation
    }

    private enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive,
        WTSConnected,
        WTSConnectQuery,
        WTSShadow,
        WTSDisconnected,
        WTSIdle,
        WTSListen,
        WTSReset,
        WTSDown,
        WTSInit
    }

    private enum WTS_INFO_CLASS
    {
        WTSInitialProgram = 0,
        WTSApplicationName = 1,
        WTSWorkingDirectory = 2,
        WTSOEMId = 3,
        WTSSessionId = 4,
        WTSUserName = 5,
        WTSWinStationName = 6,
        WTSDomainName = 7,
        WTSConnectState = 8,
        WTSClientBuildNumber = 9,
        WTSClientName = 10,
        WTSClientDirectory = 11,
        WTSClientProductId = 12,
        WTSClientHardwareId = 13,
        WTSClientAddress = 14,
        WTSClientDisplay = 15,
        WTSClientProtocolType = 16,
        WTSSessionInfoEx = 25
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTSINFOEX
    {
        public int Level;
        public WTSINFOEX_LEVEL1 Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTSINFOEX_LEVEL1
    {
        public int SessionId;
        public WTS_CONNECTSTATE_CLASS SessionState;
        public int SessionFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionID;
        public IntPtr pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("Wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(
        IntPtr hServer,
        int reserved,
        int version,
        out IntPtr ppSessionInfo,
        out int count);

    [DllImport("Wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("Wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer,
        uint sessionId,
        WTS_INFO_CLASS wtsInfoClass,
        out IntPtr ppBuffer,
        out uint pBytesReturned);

    [DllImport("Wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        SafeHandle hExistingToken,
        uint dwDesiredAccess,
        IntPtr lpTokenAttributes,
        SECURITY_IMPERSONATION_LEVEL impersonationLevel,
        TOKEN_TYPE tokenType,
        out SafeAccessTokenHandle phNewToken);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(
        out IntPtr lpEnvironment,
        SafeHandle hToken,
        bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        SafeHandle hToken,
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}