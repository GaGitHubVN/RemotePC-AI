using System.ComponentModel;

namespace RemotePC.Host.ViewModels;

/// <summary>Runtime-switchable strings (vi/en). Bind with {Binding L[key]}. Add a language by adding a dictionary.</summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    private string _lang = "vi";

    public string Language
    {
        get => _lang;
        set { _lang = value is "en" ? "en" : "vi"; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]")); }
    }

    public string this[string key] =>
        (Language == "en" ? En : Vi).TryGetValue(key, out var v) ? v : En.GetValueOrDefault(key, key);

    private static readonly Dictionary<string, string> Vi = new()
    {
        ["nav.dashboard"] = "Tổng quan", ["nav.devices"] = "Thiết bị", ["nav.sessions"] = "Phiên điều khiển", ["nav.ai"] = "AI cục bộ",
        ["nav.agent"] = "AI Agent", ["nav.files"] = "Truyền file", ["nav.security"] = "Bảo mật", ["nav.logs"] = "Nhật ký", ["nav.settings"] = "Cài đặt",
        ["dash.title"] = "Tổng quan", ["dash.subtitle"] = "Trạng thái máy tính này và các kết nối",
        ["dash.online"] = "Trực tuyến", ["dash.offline"] = "Ngoại tuyến", ["dash.lanonly"] = "Chỉ mạng LAN",
        ["dash.deviceId"] = "Mã thiết bị", ["dash.paired"] = "Thiết bị đã ghép nối", ["dash.sessions"] = "Phiên đang hoạt động",
        ["dash.localIp"] = "IP nội bộ", ["dash.internet"] = "Signaling Internet", ["dash.latency"] = "Độ trễ phiên", ["dash.quality"] = "Chất lượng stream",
        ["dash.cpu"] = "CPU", ["dash.ram"] = "RAM", ["dash.ai"] = "AI cục bộ", ["dash.model"] = "Model đang chọn", ["dash.recent"] = "Hoạt động gần đây",
        ["btn.pair"] = "Ghép nối điện thoại", ["btn.disconnectAll"] = "Ngắt tất cả", ["btn.emergency"] = "NGẮT KHẨN CẤP", ["btn.revokeAll"] = "Thu hồi tất cả thiết bị",
        ["btn.revoke"] = "Thu hồi", ["btn.rename"] = "Đổi tên", ["btn.save"] = "Lưu", ["btn.scan"] = "Quét provider", ["btn.refresh"] = "Làm mới model",
        ["btn.select"] = "Chọn model", ["btn.test"] = "Thử inference", ["btn.clearLogs"] = "Xóa nhật ký", ["btn.exportLogs"] = "Xuất nhật ký đã ẩn dữ liệu nhạy cảm",
        ["btn.cancel"] = "Hủy", ["btn.close"] = "Đóng", ["btn.approve"] = "Cho phép", ["btn.reject"] = "Từ chối", ["btn.disconnect"] = "Ngắt kết nối",
        ["btn.checkUpdate"] = "Kiểm tra cập nhật", ["btn.connect"] = "Kết nối",
        ["devices.title"] = "Thiết bị đã ghép nối", ["devices.subtitle"] = "Quyền được kiểm tra trên máy tính này cho từng yêu cầu.",
        ["devices.empty"] = "Chưa có thiết bị nào. Nhấn \"Ghép nối điện thoại\" để bắt đầu.", ["devices.maxSessions"] = "Số phiên đồng thời tối đa",
        ["perm.view_screen"] = "Xem màn hình", ["perm.control_input"] = "Điều khiển chuột/bàn phím", ["perm.use_ai"] = "Sử dụng AI",
        ["perm.ai_agent"] = "Cho AI thao tác Windows", ["perm.send_files"] = "Gửi file lên PC", ["perm.receive_files"] = "Nhận file từ PC",
        ["perm.clipboard"] = "Đồng bộ clipboard", ["perm.audio"] = "Stream âm thanh", ["perm.modify_files"] = "Đổi tên/xóa file",
        ["sessions.title"] = "Phiên điều khiển", ["sessions.empty"] = "Không có phiên nào đang hoạt động.",
        ["ai.title"] = "AI cục bộ", ["ai.subtitle"] = "AI chạy trên máy này. Điện thoại chỉ gửi yêu cầu qua kết nối đã mã hóa.",
        ["ai.providers"] = "Provider", ["ai.models"] = "Model", ["ai.ctx"] = "Context", ["ai.tools"] = "Tool calling", ["ai.vision"] = "Hình ảnh", ["ai.unknown"] = "không rõ",
        ["ai.noModel"] = "Chưa chọn model", ["ai.concurrency"] = "Số yêu cầu AI đồng thời tối đa",
        ["agent.title"] = "AI Agent", ["agent.subtitle"] = "Các tác vụ AI thực hiện trên máy này. Thao tác ghi cần điện thoại xác nhận.",
        ["agent.empty"] = "Chưa có tác vụ.", ["agent.cancel"] = "Hủy tác vụ",
        ["files.title"] = "Truyền file", ["files.subtitle"] = "File nhận được không bao giờ tự chạy và không ghi đè file sẵn có.", ["files.receiveFolder"] = "Thư mục nhận file", ["files.roots"] = "Thư mục điện thoại được duyệt",
        ["security.title"] = "Trung tâm bảo mật", ["security.lock"] = "Khóa truy cập từ xa", ["security.lockDesc"] = "Từ chối mọi phiên mới và ngắt các phiên hiện tại.",
        ["security.agentPolicy"] = "Chính sách AI Agent", ["security.autoRead"] = "Tự cho phép thao tác chỉ đọc", ["security.autoWrite"] = "Tự cho phép thao tác ghi (không khuyến nghị)", ["security.reviewPlan"] = "Xem và sửa kế hoạch trước khi chạy",
        ["logs.title"] = "Nhật ký hoạt động", ["logs.subtitle"] = "Token, khóa và mật khẩu được tự động ẩn trước khi ghi.",
        ["settings.title"] = "Cài đặt", ["settings.signaling"] = "Máy chủ signaling (wss://…) cho kết nối khác mạng", ["settings.lan"] = "Cho phép kết nối trong mạng LAN",
        ["settings.input"] = "Cho phép điều khiển chuột/bàn phím từ xa", ["settings.quality"] = "Chất lượng stream mặc định", ["settings.theme"] = "Giao diện", ["settings.language"] = "Ngôn ngữ",
        ["settings.reducedMotion"] = "Giảm chuyển động", ["settings.autostart"] = "Tự khởi chạy khi đăng nhập Windows", ["settings.retention"] = "Giữ nhật ký (ngày)",
        ["pair.title"] = "Ghép nối điện thoại", ["pair.scan"] = "Mở ứng dụng RemotePC AI trên điện thoại và quét mã QR.", ["pair.code"] = "Hoặc nhập mã một lần (trong cùng mạng LAN):", ["pair.expires"] = "Mã hết hạn sau 5 phút và chỉ dùng được một lần.",
        ["approve.title"] = "Thiết bị mới muốn kết nối", ["approve.verify"] = "Kiểm tra mã này trùng với mã trên điện thoại:", ["approve.permissions"] = "Quyền cấp cho thiết bị này:",
        ["banner.active"] = "Đang có phiên điều khiển từ xa", ["banner.agent"] = "AI Agent đang thao tác",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["nav.dashboard"] = "Dashboard", ["nav.devices"] = "Devices", ["nav.sessions"] = "Remote Sessions", ["nav.ai"] = "Local AI",
        ["nav.agent"] = "AI Agent", ["nav.files"] = "File Transfer", ["nav.security"] = "Security", ["nav.logs"] = "Activity Logs", ["nav.settings"] = "Settings",
        ["dash.title"] = "Dashboard", ["dash.subtitle"] = "This PC and its connections",
        ["dash.online"] = "Online", ["dash.offline"] = "Offline", ["dash.lanonly"] = "LAN only",
        ["dash.deviceId"] = "Device ID", ["dash.paired"] = "Paired devices", ["dash.sessions"] = "Active sessions",
        ["dash.localIp"] = "Local IP", ["dash.internet"] = "Internet signaling", ["dash.latency"] = "Session latency", ["dash.quality"] = "Stream quality",
        ["dash.cpu"] = "CPU", ["dash.ram"] = "RAM", ["dash.ai"] = "Local AI", ["dash.model"] = "Selected model", ["dash.recent"] = "Recent activity",
        ["btn.pair"] = "Pair a phone", ["btn.disconnectAll"] = "Disconnect all", ["btn.emergency"] = "EMERGENCY DISCONNECT", ["btn.revokeAll"] = "Revoke all devices",
        ["btn.revoke"] = "Revoke", ["btn.rename"] = "Rename", ["btn.save"] = "Save", ["btn.scan"] = "Scan providers", ["btn.refresh"] = "Refresh models",
        ["btn.select"] = "Select model", ["btn.test"] = "Test inference", ["btn.clearLogs"] = "Clear logs", ["btn.exportLogs"] = "Export redacted logs",
        ["btn.cancel"] = "Cancel", ["btn.close"] = "Close", ["btn.approve"] = "Allow", ["btn.reject"] = "Reject", ["btn.disconnect"] = "Disconnect",
        ["btn.checkUpdate"] = "Check for updates", ["btn.connect"] = "Connect",
        ["devices.title"] = "Paired devices", ["devices.subtitle"] = "Permissions are enforced on this PC for every request.",
        ["devices.empty"] = "No devices yet. Click \"Pair a phone\" to start.", ["devices.maxSessions"] = "Max concurrent sessions",
        ["perm.view_screen"] = "View screen", ["perm.control_input"] = "Control mouse/keyboard", ["perm.use_ai"] = "Use AI",
        ["perm.ai_agent"] = "Let AI operate Windows", ["perm.send_files"] = "Send files to PC", ["perm.receive_files"] = "Receive files from PC",
        ["perm.clipboard"] = "Clipboard sync", ["perm.audio"] = "Audio streaming", ["perm.modify_files"] = "Rename/delete files",
        ["sessions.title"] = "Remote sessions", ["sessions.empty"] = "No active sessions.",
        ["ai.title"] = "Local AI", ["ai.subtitle"] = "AI runs on this PC. Phones only send requests over the encrypted session.",
        ["ai.providers"] = "Providers", ["ai.models"] = "Models", ["ai.ctx"] = "Context", ["ai.tools"] = "Tool calling", ["ai.vision"] = "Vision", ["ai.unknown"] = "unknown",
        ["ai.noModel"] = "No model selected", ["ai.concurrency"] = "Max concurrent AI requests",
        ["agent.title"] = "AI Agent", ["agent.subtitle"] = "Tasks the AI performs on this PC. Write actions must be confirmed on the phone.",
        ["agent.empty"] = "No tasks yet.", ["agent.cancel"] = "Cancel task",
        ["files.title"] = "File transfer", ["files.subtitle"] = "Received files are never executed and never overwrite existing files.", ["files.receiveFolder"] = "Receive folder", ["files.roots"] = "Folders phones may browse",
        ["security.title"] = "Security center", ["security.lock"] = "Lock remote access", ["security.lockDesc"] = "Reject new sessions and end current ones.",
        ["security.agentPolicy"] = "AI agent policy", ["security.autoRead"] = "Auto-approve read-only actions", ["security.autoWrite"] = "Auto-approve write actions (not recommended)", ["security.reviewPlan"] = "Review and edit the plan before running",
        ["logs.title"] = "Activity logs", ["logs.subtitle"] = "Tokens, keys and passwords are redacted before writing.",
        ["settings.title"] = "Settings", ["settings.signaling"] = "Signaling server (wss://…) for connections across networks", ["settings.lan"] = "Allow LAN connections",
        ["settings.input"] = "Allow remote mouse/keyboard control", ["settings.quality"] = "Default stream quality", ["settings.theme"] = "Theme", ["settings.language"] = "Language",
        ["settings.reducedMotion"] = "Reduce motion", ["settings.autostart"] = "Start when I sign in to Windows", ["settings.retention"] = "Keep logs (days)",
        ["pair.title"] = "Pair a phone", ["pair.scan"] = "Open RemotePC AI on your phone and scan the QR code.", ["pair.code"] = "Or enter the one-time code (same LAN):", ["pair.expires"] = "The code expires in 5 minutes and works once.",
        ["approve.title"] = "A new device wants to connect", ["approve.verify"] = "Check this code matches the code on the phone:", ["approve.permissions"] = "Permissions for this device:",
        ["banner.active"] = "Remote session active", ["banner.agent"] = "AI agent is operating",
    };
}
