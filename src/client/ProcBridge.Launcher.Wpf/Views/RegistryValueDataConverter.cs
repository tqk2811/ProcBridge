using System.Globalization;
using System.Windows.Data;
using ProcBridge.Agent.Explorer.Enums;
using ProcBridge.Agent.Explorer.Models;

namespace ProcBridge.Launcher.Wpf.Views;

/// <summary>Shows the data field of a <see cref="RegistryValue"/> that its type uses.</summary>
public sealed class RegistryValueDataConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is RegistryValue v ? Format(v) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    public static string Format(RegistryValue value) => value.Type switch
    {
        RegistryValueType.String or RegistryValueType.ExpandString => value.Text ?? string.Empty,
        RegistryValueType.DWord or RegistryValueType.QWord => value.Number is { } n ? $"0x{n:X} ({n})" : string.Empty,
        RegistryValueType.MultiString => string.Join(" | ", value.Strings ?? []),
        _ => value.Bytes is null ? string.Empty : System.Convert.ToHexString(value.Bytes.AsSpan(0, Math.Min(value.Bytes.Length, 64))) + (value.Bytes.Length > 64 ? "…" : string.Empty),
    };
}
