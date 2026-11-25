using ProcBridge.Agent.Explorer.Enums;

namespace ProcBridge.Agent.Wpf.Core;

/// <summary>UI text of each <see cref="ExplorerPermissions"/> flag (a test checks every flag has one).</summary>
public static class ExplorerPermissionLabels
{
    private static readonly IReadOnlyDictionary<ExplorerPermissions, string> _labels = new Dictionary<ExplorerPermissions, string>
    {
        [ExplorerPermissions.FileRead] = "Duyệt và tải file trong các gốc file",
        [ExplorerPermissions.FileWrite] = "Tạo, đổi tên, xoá file trong các gốc file",
        [ExplorerPermissions.ProcessRead] = "Xem danh sách process",
        [ExplorerPermissions.ProcessKill] = "Kill process",
        [ExplorerPermissions.RegistryRead] = "Đọc registry trong các gốc registry",
        [ExplorerPermissions.RegistryWrite] = "Sửa, xoá khoá và giá trị registry trong các gốc registry",
        [ExplorerPermissions.ServiceRead] = "Xem danh sách service",
        [ExplorerPermissions.ServiceControl] = "Start, stop, restart service",
        [ExplorerPermissions.SystemInfo] = "Xem thông tin hệ thống",
    };

    /// <summary>Every single flag, in declaration order, without <see cref="ExplorerPermissions.None"/>.</summary>
    public static IReadOnlyList<ExplorerPermissions> Flags { get; } = Enum.GetValues<ExplorerPermissions>().Where(x => x != ExplorerPermissions.None).ToArray();

    /// <exception cref="ArgumentOutOfRangeException">The flag has no label.</exception>
    public static string Describe(ExplorerPermissions flag)
        => _labels.TryGetValue(flag, out string? label) ? label : throw new ArgumentOutOfRangeException(nameof(flag), flag, "No label for this permission.");

    /// <summary>True for flags that change machine B (they are shown as dangerous).</summary>
    public static bool IsWrite(ExplorerPermissions flag)
        => flag is ExplorerPermissions.FileWrite or ExplorerPermissions.ProcessKill or ExplorerPermissions.RegistryWrite or ExplorerPermissions.ServiceControl;
}
