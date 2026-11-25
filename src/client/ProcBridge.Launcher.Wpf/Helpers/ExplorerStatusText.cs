using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Launcher.Wpf.Helpers;

/// <summary>User text for a refused Remote Explorer operation (UI labels live here, not on the enum).</summary>
public static class ExplorerStatusText
{
    public static string Describe(ExplorerStatus status, string? detail = null)
    {
        string text = status switch
        {
            ExplorerStatus.Ok => "Thành công.",
            ExplorerStatus.PermissionDenied => "Máy B không cấp quyền này cho máy này (hồ sơ quyền của máy B).",
            ExplorerStatus.OutOfScope => "Nằm ngoài phạm vi máy B cho phép duyệt.",
            ExplorerStatus.NotFound => "Không tìm thấy trên máy B.",
            ExplorerStatus.InvalidArgument => "Yêu cầu không hợp lệ.",
            ExplorerStatus.AccessDenied => "Windows trên máy B từ chối (tài khoản chạy Agent không đủ quyền).",
            ExplorerStatus.AlreadyExists => "Đã tồn tại trên máy B.",
            ExplorerStatus.Failed => "Máy B báo lỗi khi thực hiện.",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown Explorer status."),
        };
        return string.IsNullOrWhiteSpace(detail) || status == ExplorerStatus.Ok ? text : $"{text} ({detail})";
    }

    /// <summary>Text shown instead of calling when the profile of machine B lacks <paramref name="permission"/>.</summary>
    public static string NotGranted(ExplorerPermissions permission) => $"Máy B chưa cấp quyền {permission} cho máy này.";
}
