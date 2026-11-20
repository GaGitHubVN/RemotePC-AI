import 'app_state.dart';

/// Lightweight runtime localisation (vi/en). Add a language by adding a map.
String tr(String key) {
  final lang = AppState.instance.language;
  return (lang == 'en' ? _en : _vi)[key] ?? _en[key] ?? key;
}

const _vi = <String, String>{
  'app': 'RemotePC AI',
  'home': 'Máy tính', 'ai': 'AI', 'files': 'File', 'settings': 'Cài đặt',
  'add_pc': 'Thêm máy tính', 'no_pcs': 'Chưa ghép nối máy tính nào', 'no_pcs_sub': 'Mở RemotePC AI trên Windows, chọn "Ghép nối điện thoại" rồi quét mã QR.',
  'connect': 'Kết nối', 'disconnect': 'Ngắt kết nối', 'chat': 'Chat AI', 'tasks': 'Tác vụ AI', 'retry_now': 'Thử lại ngay',
  'connecting': 'Đang kết nối…', 'connected': 'Đã kết nối', 'reconnecting': 'Đang kết nối lại…', 'failed': 'Kết nối thất bại', 'offline': 'Chưa kết nối',
  'scan_qr': 'Quét mã QR', 'enter_code': 'Nhập mã một lần', 'find_lan': 'Tìm máy tính trong mạng LAN', 'pairing': 'Đang ghép nối…',
  'verify_code': 'So sánh mã này với mã hiển thị trên máy tính, rồi bấm "Cho phép" trên máy tính.', 'paired': 'Đã ghép nối',
  'onb_title': 'Điều khiển PC và dùng AI trên máy tính của bạn', 'onb_1': 'Xem và điều khiển màn hình Windows qua kết nối mã hóa đầu-cuối.',
  'onb_2': 'Trò chuyện với AI chạy ngay trên PC — không gửi lên đám mây.', 'onb_3': 'Cho AI thao tác Windows, luôn hỏi bạn trước các thay đổi.',
  'perm_title': 'Quyền ứng dụng sẽ hỏi', 'perm_camera': 'Camera — chỉ để quét mã QR ghép nối.', 'perm_network': 'Mạng cục bộ — để tìm và kết nối PC trong cùng Wi-Fi.',
  'perm_files': 'Tệp — chỉ khi bạn chọn file để gửi.', 'continue': 'Tiếp tục', 'start': 'Bắt đầu',
  'new_chat': 'Cuộc trò chuyện mới', 'search': 'Tìm kiếm', 'type_message': 'Nhắn cho AI trên PC…', 'stop': 'Dừng', 'regenerate': 'Tạo lại', 'copy': 'Sao chép', 'edit': 'Sửa',
  'rename': 'Đổi tên', 'delete': 'Xóa', 'model': 'Model', 'select_model': 'Chọn model', 'agent_mode': 'Cho AI thao tác PC', 'attach_file': 'Đính kèm file', 'attach_image': 'Đính kèm ảnh',
  'approve': 'Cho phép', 'reject': 'Từ chối', 'cancel': 'Hủy', 'pause': 'Tạm dừng', 'resume': 'Tiếp tục', 'approval_needed': 'AI cần bạn xác nhận',
  'upload': 'Tải lên', 'download': 'Tải xuống', 'new_folder': 'Thư mục mới', 'confirm_delete': 'Xóa các mục đã chọn? File được chuyển vào Thùng rác trên PC.',
  'keyboard': 'Bàn phím', 'trackpad': 'Trackpad', 'touch': 'Chạm trực tiếp', 'display': 'Màn hình', 'screenshot': 'Chụp màn hình', 'quality': 'Chất lượng',
  'diagnostics': 'Chẩn đoán kết nối', 'security': 'Bảo mật', 'about': 'Giới thiệu', 'language': 'Ngôn ngữ', 'theme': 'Giao diện', 'reduce_motion': 'Giảm chuyển động',
  'sensitivity': 'Độ nhạy con trỏ', 'clipboard': 'Clipboard', 'send_clipboard': 'Gửi clipboard sang PC', 'get_clipboard': 'Lấy clipboard từ PC',
  'remove_pc': 'Xóa máy tính này', 'reset_identity': 'Đặt lại danh tính thiết bị', 'history': 'Lịch sử phiên', 'permissions': 'Quyền trên PC',
  'not_allowed': 'Quyền này chưa được cấp trên máy tính.', 'model_no_tools': 'Model này không hỗ trợ tool calling nên không thể thao tác PC.',
};

const _en = <String, String>{
  'app': 'RemotePC AI',
  'home': 'PCs', 'ai': 'AI', 'files': 'Files', 'settings': 'Settings',
  'add_pc': 'Add a PC', 'no_pcs': 'No PCs paired yet', 'no_pcs_sub': 'Open RemotePC AI on Windows, choose "Pair a phone" and scan the QR code.',
  'connect': 'Connect', 'disconnect': 'Disconnect', 'chat': 'AI Chat', 'tasks': 'AI Tasks', 'retry_now': 'Retry now',
  'connecting': 'Connecting…', 'connected': 'Connected', 'reconnecting': 'Reconnecting…', 'failed': 'Connection failed', 'offline': 'Not connected',
  'scan_qr': 'Scan QR code', 'enter_code': 'Enter one-time code', 'find_lan': 'Find PCs on this network', 'pairing': 'Pairing…',
  'verify_code': 'Compare this code with the one shown on the PC, then click "Allow" on the PC.', 'paired': 'Paired',
  'onb_title': 'Control your PC and use the AI that runs on it', 'onb_1': 'See and control your Windows screen over an end-to-end encrypted link.',
  'onb_2': 'Chat with AI that runs on your PC — nothing goes to the cloud.', 'onb_3': 'Let the AI operate Windows, always asking before changes.',
  'perm_title': 'Permissions the app will ask for', 'perm_camera': 'Camera — only to scan the pairing QR code.', 'perm_network': 'Local network — to find and connect to PCs on your Wi-Fi.',
  'perm_files': 'Files — only when you choose a file to send.', 'continue': 'Continue', 'start': 'Get started',
  'new_chat': 'New chat', 'search': 'Search', 'type_message': 'Message the AI on your PC…', 'stop': 'Stop', 'regenerate': 'Regenerate', 'copy': 'Copy', 'edit': 'Edit',
  'rename': 'Rename', 'delete': 'Delete', 'model': 'Model', 'select_model': 'Select model', 'agent_mode': 'Let AI operate the PC', 'attach_file': 'Attach file', 'attach_image': 'Attach image',
  'approve': 'Allow', 'reject': 'Reject', 'cancel': 'Cancel', 'pause': 'Pause', 'resume': 'Resume', 'approval_needed': 'The AI needs your confirmation',
  'upload': 'Upload', 'download': 'Download', 'new_folder': 'New folder', 'confirm_delete': 'Delete the selected items? Files go to the Recycle Bin on the PC.',
  'keyboard': 'Keyboard', 'trackpad': 'Trackpad', 'touch': 'Direct touch', 'display': 'Display', 'screenshot': 'Screenshot', 'quality': 'Quality',
  'diagnostics': 'Connection diagnostics', 'security': 'Security', 'about': 'About', 'language': 'Language', 'theme': 'Theme', 'reduce_motion': 'Reduce motion',
  'sensitivity': 'Pointer sensitivity', 'clipboard': 'Clipboard', 'send_clipboard': 'Send clipboard to PC', 'get_clipboard': 'Get clipboard from PC',
  'remove_pc': 'Remove this PC', 'reset_identity': 'Reset device identity', 'history': 'Session history', 'permissions': 'Permissions on the PC',
  'not_allowed': 'This permission has not been granted on the PC.', 'model_no_tools': 'This model does not support tool calling, so it cannot operate the PC.',
};
