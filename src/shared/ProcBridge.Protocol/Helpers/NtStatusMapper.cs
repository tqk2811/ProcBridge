using ProcBridge.Protocol.Enums;

namespace ProcBridge.Protocol.Helpers;

/// <summary>Maps .NET exceptions raised while serving a request to the NTSTATUS a local caller would get.</summary>
public static class NtStatusMapper
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorFileExists = 80;
    private const int ErrorInvalidName = 123;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorDirectory = 267;

    public static NtStatus FromException(Exception exception) => exception switch
    {
        ProtocolStatusException e => e.Status,
        FileNotFoundException => NtStatus.ObjectNameNotFound,
        DirectoryNotFoundException => NtStatus.ObjectPathNotFound,
        UnauthorizedAccessException => NtStatus.AccessDenied,
        PathTooLongException => NtStatus.ObjectNameInvalid,
        OperationCanceledException => NtStatus.Cancelled,
        TimeoutException => NtStatus.IoTimeout,
        ArgumentException => NtStatus.ObjectNameInvalid,
        IOException e => FromWin32Error(e.HResult & 0xFFFF, (e.HResult >> 16 & 0x1FFF) == 7),
        _ => NtStatus.Unsuccessful,
    };

    /// <param name="isWin32">True when the HRESULT facility is FACILITY_WIN32, so the low word is a Win32 error.</param>
    private static NtStatus FromWin32Error(int code, bool isWin32)
    {
        if (!isWin32)
            return NtStatus.Unsuccessful;
        return code switch
        {
            ErrorFileNotFound => NtStatus.ObjectNameNotFound,
            ErrorPathNotFound => NtStatus.ObjectPathNotFound,
            ErrorAccessDenied => NtStatus.AccessDenied,
            ErrorSharingViolation or ErrorLockViolation => NtStatus.SharingViolation,
            ErrorFileExists or ErrorAlreadyExists => NtStatus.ObjectNameCollision,
            ErrorInvalidName => NtStatus.ObjectNameInvalid,
            ErrorDirectory => NtStatus.NotADirectory,
            _ => NtStatus.Unsuccessful,
        };
    }
}
