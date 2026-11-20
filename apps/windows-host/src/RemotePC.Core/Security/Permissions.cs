namespace RemotePC.Core.Security;

/// <summary>Per-device permissions. Enforced on the host for every request — never trusted from the client.</summary>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "Domain term; not a CAS permission type")]
public enum Permission
{
    None = 0,
    ViewScreen = 1 << 0,
    ControlInput = 1 << 1,
    UseAi = 1 << 2,
    AiAgent = 1 << 3,
    /// <summary>Phone → PC uploads (and creating folders inside allowed roots).</summary>
    SendFiles = 1 << 4,
    /// <summary>PC → phone downloads and browsing allowed roots.</summary>
    ReceiveFiles = 1 << 5,
    Clipboard = 1 << 6,
    Audio = 1 << 7,
    /// <summary>Rename/delete inside allowed roots. Deletes still require confirmation on the phone.</summary>
    ModifyFiles = 1 << 8,
}

public static class PermissionNames
{
    private static readonly (Permission P, string Name)[] Map =
    [
        (Permission.ViewScreen, "view_screen"),
        (Permission.ControlInput, "control_input"),
        (Permission.UseAi, "use_ai"),
        (Permission.AiAgent, "ai_agent"),
        (Permission.SendFiles, "send_files"),
        (Permission.ReceiveFiles, "receive_files"),
        (Permission.Clipboard, "clipboard"),
        (Permission.Audio, "audio"),
        (Permission.ModifyFiles, "modify_files"),
    ];

    public static Permission DefaultForNewDevice =>
        Permission.ViewScreen | Permission.ControlInput | Permission.UseAi;

    public static IEnumerable<Permission> All => Map.Select(m => m.P);

    public static string[] ToNames(Permission p) => Map.Where(m => p.HasFlag(m.P)).Select(m => m.Name).ToArray();

    public static string NameOf(Permission p) => Map.First(m => m.P == p).Name;

    public static Permission FromNames(IEnumerable<string> names)
    {
        var r = Permission.None;
        foreach (var n in names)
        {
            foreach (var m in Map)
                if (m.Name == n) r |= m.P;
        }
        return r;
    }
}
