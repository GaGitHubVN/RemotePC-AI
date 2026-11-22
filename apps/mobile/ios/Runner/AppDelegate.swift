import Flutter
import SwiftUI
import UIKit

@main
@objc class AppDelegate: FlutterAppDelegate, FlutterImplicitEngineDelegate {
  override func application(
    _ application: UIApplication,
    didFinishLaunchingWithOptions launchOptions: [UIApplication.LaunchOptionsKey: Any]?
  ) -> Bool {
    return super.application(application, didFinishLaunchingWithOptions: launchOptions)
  }

  func didInitializeImplicitFlutterEngine(_ engineBridge: FlutterImplicitEngineBridge) {
    GeneratedPluginRegistrant.register(with: engineBridge.pluginRegistry)
    guard let registrar = engineBridge.pluginRegistry.registrar(forPlugin: "RemotePCNative") else { return }
    NativeShell.register(with: registrar)
  }
}

// MARK: - Native shell ------------------------------------------------------------------------
//
// One entry point for everything the Dart UI needs from UIKit:
//  • ai.remotepc/platform  — OS version and whether Liquid Glass is available (decided with #available, not strings)
//  • ai.remotepc/dialogs   — UIAlertController alerts / action sheets / text prompts (system look on iOS 18 and 26)
//  • ai.remotepc/tabbar    — iOS 26+: a real UITabBarController hosting the Flutter view (system Liquid Glass tab bar)
//  • platform views        — iOS 26+: SwiftUI glass buttons, bar button groups and the remote-control toolbar
//
// Every Liquid Glass API is behind `if #available(iOS 26.0, *)`. On iOS 18–25 the Dart side never asks for them.

final class NativeShell: NSObject {
  static let shared = NativeShell()
  var tabs: NativeTabController?
  var tabChannel: FlutterMethodChannel?

  static var glassAvailable: Bool {
    if #available(iOS 26.0, *) { return true }
    return false
  }

  static func register(with registrar: FlutterPluginRegistrar) {
    let m = registrar.messenger()

    FlutterMethodChannel(name: "ai.remotepc/platform", binaryMessenger: m).setMethodCallHandler { call, result in
      switch call.method {
      case "info":
        let v = ProcessInfo.processInfo.operatingSystemVersion
        result(["osMajor": v.majorVersion, "osMinor": v.minorVersion, "glass": glassAvailable,
                "reduceTransparency": UIAccessibility.isReduceTransparencyEnabled])
      case "setStyle":
        let mode = (call.arguments as? String) ?? "system"
        for w in keyWindows() {
          w.overrideUserInterfaceStyle = mode == "dark" ? .dark : mode == "light" ? .light : .unspecified
        }
        result(nil)
      default: result(FlutterMethodNotImplemented)
      }
    }

    FlutterMethodChannel(name: "ai.remotepc/dialogs", binaryMessenger: m).setMethodCallHandler { call, result in
      Dialogs.handle(call, result)
    }

    let tabChannel = FlutterMethodChannel(name: "ai.remotepc/tabbar", binaryMessenger: m)
    shared.tabChannel = tabChannel
    tabChannel.setMethodCallHandler { call, result in shared.handleTab(call, result) }

    registrar.register(GlassViewFactory(messenger: m, kind: .toolbar), withId: "ai.remotepc/glass-toolbar")
    registrar.register(GlassViewFactory(messenger: m, kind: .barGroup), withId: "ai.remotepc/glass-bar")
    registrar.register(GlassViewFactory(messenger: m, kind: .button), withId: "ai.remotepc/glass-button")
  }

  static func keyWindows() -> [UIWindow] {
    UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.flatMap { $0.windows }
  }

  static func topController() -> UIViewController? {
    var top = keyWindows().first(where: { $0.isKeyWindow })?.rootViewController ?? keyWindows().first?.rootViewController
    while let p = top?.presentedViewController { top = p }
    return top
  }

  // ---- tab bar ----
  private func handleTab(_ call: FlutterMethodCall, _ result: @escaping FlutterResult) {
    let args = call.arguments as? [String: Any] ?? [:]
    switch call.method {
    case "configure":
      guard NativeShell.glassAvailable else { result(false); return }
      let items = (args["items"] as? [[String: Any]]) ?? []
      let selected = args["selected"] as? Int ?? 0
      NSLog("RemotePCNative: tabbar.configure items=%d windows=%d", items.count, NativeShell.keyWindows().count)
      for w in NativeShell.keyWindows() { NSLog("RemotePCNative: window key=%d hidden=%d root=%@", w.isKeyWindow ? 1 : 0, w.isHidden ? 1 : 0, String(describing: type(of: w.rootViewController))) }
      if tabs == nil {
        guard let window = NativeShell.keyWindows().first(where: { $0.rootViewController is FlutterViewController }),
              let flutter = window.rootViewController as? FlutterViewController else { NSLog("RemotePCNative: no FlutterViewController root"); result(false); return }
        let t = NativeTabController(flutter: flutter) { [weak self] i in self?.tabChannel?.invokeMethod("selected", arguments: i) }
        tabs = t
        t.configure(items: items, selected: selected)
        window.rootViewController = t
        t.embedFlutter(at: selected, initial: true)
        NSLog("RemotePCNative: root swapped; window key=%d hidden=%d flutterInWindow=%d", window.isKeyWindow ? 1 : 0, window.isHidden ? 1 : 0, flutter.view.window != nil ? 1 : 0)
      } else {
        tabs?.configure(items: items, selected: selected)
      }
      result(true)
    case "select":
      tabs?.selectTab(args["index"] as? Int ?? 0)
      result(nil)
    case "setHidden":
      tabs?.setBarHidden(args["hidden"] as? Bool ?? false, animated: args["animated"] as? Bool ?? true)
      result(nil)
    default:
      result(FlutterMethodNotImplemented)
    }
  }
}

// MARK: - Tab bar controller hosting the single Flutter view (iOS 26+) ------------------------------
//
// Apple's guidance: use the system UITabBarController to get the Liquid Glass tab bar (a standalone UITabBar does not
// get the full treatment). Flutter has one FlutterViewController, so each tab is an empty container and the Flutter
// controller is moved into the selected container. Appearance forwarding is disabled on the containers so a tab
// switch never pauses the Flutter surface; Dart switches the visible tab content itself.

final class TabContainer: UIViewController {
  override var shouldAutomaticallyForwardAppearanceMethods: Bool { false }
  override func loadView() {
    view = UIView()
    view.backgroundColor = .clear
  }
}

final class NativeTabController: UITabBarController, UITabBarControllerDelegate {
  private let flutter: FlutterViewController
  private let onSelect: (Int) -> Void
  private var containers: [TabContainer] = []

  init(flutter: FlutterViewController, onSelect: @escaping (Int) -> Void) {
    self.flutter = flutter
    self.onSelect = onSelect
    super.init(nibName: nil, bundle: nil)
    delegate = self
  }

  @available(*, unavailable) required init?(coder: NSCoder) { fatalError() }

  func configure(items: [[String: Any]], selected: Int) {
    if containers.count != items.count {
      containers = items.map { _ in TabContainer() }
      setViewControllers(containers, animated: false)
    }
    for (i, it) in items.enumerated() {
      let title = it["title"] as? String ?? ""
      let symbol = it["symbol"] as? String ?? "circle"
      let item = UITabBarItem(title: title, image: UIImage(systemName: symbol),
                              selectedImage: UIImage(systemName: (it["selectedSymbol"] as? String) ?? symbol))
      item.accessibilityIdentifier = "tab-\(i)"
      containers[i].tabBarItem = item
    }
    tabBar.tintColor = UIColor(red: 0.31, green: 0.27, blue: 0.90, alpha: 1)
    if selectedIndex != selected, selected < containers.count {
      selectedIndex = selected
      embedFlutter(at: selected, initial: false)
    }
  }

  func embedFlutter(at index: Int, initial: Bool) {
    guard index < containers.count else { return }
    let target = containers[index]
    if flutter.parent === target { return }
    if flutter.parent != nil {
      flutter.willMove(toParent: nil)
      flutter.view.removeFromSuperview()
      flutter.removeFromParent()
    }
    target.loadViewIfNeeded()
    target.addChild(flutter)
    flutter.view.frame = target.view.bounds
    flutter.view.autoresizingMask = [.flexibleWidth, .flexibleHeight]
    target.view.addSubview(flutter.view)
    flutter.didMove(toParent: target)
    if initial {
      // The root swap made UIKit send viewDidDisappear to the Flutter controller (surface stopped). Containers do
      // not forward appearance, so bring it back exactly once.
      flutter.beginAppearanceTransition(true, animated: false)
      flutter.endAppearanceTransition()
    }
  }

  func selectTab(_ i: Int) {
    guard i < containers.count, i != selectedIndex else { return }
    selectedIndex = i
    embedFlutter(at: i, initial: false)
  }

  func setBarHidden(_ hidden: Bool, animated: Bool) {
    if isTabBarHidden != hidden { setTabBarHidden(hidden, animated: animated) }
  }

  func tabBarController(_ tabBarController: UITabBarController, shouldSelect viewController: UIViewController) -> Bool {
    guard let i = containers.firstIndex(where: { $0 === viewController }) else { return false }
    embedFlutter(at: i, initial: false)
    onSelect(i)
    return true
  }
}

// MARK: - Dialogs (UIAlertController) -----------------------------------------------------------

enum Dialogs {
  static func handle(_ call: FlutterMethodCall, _ result: @escaping FlutterResult) {
    guard let args = call.arguments as? [String: Any], let top = NativeShell.topController() else { result(nil); return }
    let title = args["title"] as? String
    let message = args["message"] as? String
    let sheet = call.method == "actionSheet"
    let alert = UIAlertController(title: title, message: message, preferredStyle: sheet ? .actionSheet : .alert)
    if call.method == "prompt" {
      alert.addTextField { tf in
        tf.text = args["text"] as? String
        tf.placeholder = args["placeholder"] as? String
        tf.clearButtonMode = .whileEditing
        tf.autocapitalizationType = .sentences
      }
    }
    var finished = false
    func finish(_ v: Any?) {
      if finished { return }
      finished = true
      result(v)
    }
    for a in (args["actions"] as? [[String: Any]]) ?? [] {
      let id = a["id"] as? String ?? ""
      let style: UIAlertAction.Style = switch (a["style"] as? String) ?? "" {
      case "cancel": .cancel
      case "destructive": .destructive
      default: .default
      }
      let action = UIAlertAction(title: a["title"] as? String, style: style) { [weak alert] _ in
        if call.method == "prompt" {
          finish(style == .cancel ? nil : ["id": id, "text": alert?.textFields?.first?.text ?? ""])
        } else {
          finish(style == .cancel && a["returnsId"] as? Bool != true ? nil : id)
        }
      }
      alert.addAction(action)
      if a["preferred"] as? Bool == true { alert.preferredAction = action }
    }
    if sheet, let pop = alert.popoverPresentationController {
      pop.sourceView = top.view
      pop.sourceRect = CGRect(x: top.view.bounds.midX, y: top.view.bounds.maxY - 80, width: 1, height: 1)
      pop.permittedArrowDirections = []
    }
    top.present(alert, animated: true)
  }
}

// MARK: - SwiftUI glass platform views (iOS 26+) ----------------------------------------------------

struct GlassItem: Identifiable, Equatable {
  let id: String
  let symbol: String
  let label: String
  let active: Bool
  let prominent: Bool
  let destructive: Bool

  static func parse(_ args: Any?) -> [GlassItem] {
    guard let dict = args as? [String: Any], let actions = dict["actions"] as? [[String: Any]] else { return [] }
    return actions.compactMap { a in
      guard let id = a["id"] as? String else { return nil }
      return GlassItem(id: id, symbol: a["symbol"] as? String ?? "circle", label: a["label"] as? String ?? id,
                       active: a["active"] as? Bool ?? false, prominent: a["prominent"] as? Bool ?? false,
                       destructive: a["destructive"] as? Bool ?? false)
    }
  }
}

final class GlassModel: ObservableObject {
  @Published var items: [GlassItem]
  @Published var title: String
  @Published var enabled: Bool
  var onTap: (String) -> Void = { _ in }
  init(args: Any?) {
    items = GlassItem.parse(args)
    let d = args as? [String: Any]
    title = d?["title"] as? String ?? ""
    enabled = d?["enabled"] as? Bool ?? true
  }
  func update(_ args: Any?) {
    items = GlassItem.parse(args)
    let d = args as? [String: Any]
    title = d?["title"] as? String ?? title
    enabled = d?["enabled"] as? Bool ?? enabled
  }
}

enum GlassKind { case toolbar, barGroup, button }

final class GlassViewFactory: NSObject, FlutterPlatformViewFactory {
  private let messenger: FlutterBinaryMessenger
  private let kind: GlassKind
  init(messenger: FlutterBinaryMessenger, kind: GlassKind) {
    self.messenger = messenger
    self.kind = kind
  }

  func createArgsCodec() -> FlutterMessageCodec & NSObjectProtocol { FlutterStandardMessageCodec.sharedInstance() }

  func create(withFrame frame: CGRect, viewIdentifier viewId: Int64, arguments args: Any?) -> FlutterPlatformView {
    GlassPlatformView(frame: frame, viewId: viewId, args: args, messenger: messenger, kind: kind)
  }
}

final class GlassPlatformView: NSObject, FlutterPlatformView {
  private let host: UIView
  private let model: GlassModel
  private let channel: FlutterMethodChannel
  private var controller: UIViewController?

  init(frame: CGRect, viewId: Int64, args: Any?, messenger: FlutterBinaryMessenger, kind: GlassKind) {
    model = GlassModel(args: args)
    channel = FlutterMethodChannel(name: "ai.remotepc/glass/\(viewId)", binaryMessenger: messenger)
    if #available(iOS 26.0, *) {
      let root: AnyView = switch kind {
      case .toolbar: AnyView(GlassToolbarView(model: model))
      case .barGroup: AnyView(GlassBarGroupView(model: model))
      case .button: AnyView(GlassButtonView(model: model))
      }
      let c = UIHostingController(rootView: root)
      c.view.backgroundColor = .clear
      c.view.frame = frame
      c.sizingOptions = []
      controller = c
      host = c.view
    } else {
      host = UIView(frame: frame) // never reached: Dart only creates these views when glass is available
    }
    super.init()
    model.onTap = { [weak self] id in self?.channel.invokeMethod("tap", arguments: id) }
    channel.setMethodCallHandler { [weak self] call, result in
      if call.method == "update" { self?.model.update(call.arguments) }
      result(nil)
    }
  }

  func view() -> UIView { host }
}

private let brand = Color(red: 0.31, green: 0.27, blue: 0.90)

/// Floating remote-desktop toolbar: interactive glass circles merged by a GlassEffectContainer.
@available(iOS 26.0, *)
struct GlassToolbarView: View {
  @ObservedObject var model: GlassModel
  @Namespace private var ns

  var body: some View {
    // Container spacing < item spacing: neighbours stay separate circles (no tinted blob bleeding into the next one).
    GlassEffectContainer(spacing: 4) {
      // One centered row whose button size follows the available width (44 pt down to 34 pt on narrow phones).
      // No ScrollView: it delays touches on the buttons inside it (~150 ms, delaysContentTouches). No
      // ViewThatFits either: its candidates would repeat the same glassEffectIDs.
      GeometryReader { geo in
        let n = CGFloat(max(model.items.count, 1))
        let size = min(44, max(34, (geo.size.width - 16) / (n * 1.15)))
        let spacing = min(10, max(3, (geo.size.width - 16 - n * size) / max(n - 1, 1)))
        row(size: size, spacing: spacing)
          .frame(width: geo.size.width, height: geo.size.height)
      }
    }
    .frame(maxWidth: .infinity, maxHeight: .infinity)
  }

  private func row(size: CGFloat, spacing: CGFloat) -> some View {
    HStack(spacing: spacing) {
      ForEach(model.items) { item in
        Button { model.onTap(item.id) } label: {
          Image(systemName: item.symbol)
            .font(.system(size: size * 0.39, weight: .semibold))
            .frame(width: size, height: size)
            .foregroundStyle(item.destructive ? Color.red : (item.active ? Color.white : Color.primary))
        }
        .accessibilityLabel(item.label)
        .accessibilityIdentifier("glass-\(item.id)")
        .buttonStyle(.plain)
        .glassEffect(item.active ? .regular.tint(brand).interactive() : .regular.interactive(), in: .circle)
        .glassEffectID(item.id, in: ns)
      }
    }
    .padding(.horizontal, spacing)
  }
}

/// Navigation-bar items grouped in one glass capsule (iOS 26 bar button style), or a single glass circle.
@available(iOS 26.0, *)
struct GlassBarGroupView: View {
  @ObservedObject var model: GlassModel

  var body: some View {
    HStack(spacing: 0) {
      ForEach(model.items) { item in
        Button { model.onTap(item.id) } label: {
          Image(systemName: item.symbol)
            .font(.system(size: 17, weight: .medium))
            .frame(width: 44, height: 44)
            .contentShape(Rectangle())
            .foregroundStyle(item.destructive ? Color.red : (item.prominent ? Color.white : Color.primary))
        }
        .accessibilityLabel(item.label)
        .accessibilityIdentifier("glass-\(item.id)")
        .buttonStyle(.plain)
      }
    }
    .glassEffect(model.items.first?.prominent == true ? .regular.tint(brand).interactive() : .regular.interactive(), in: .capsule)
    .frame(maxWidth: .infinity, maxHeight: .infinity)
  }
}

/// Full-width primary/secondary action (`.glassProminent` / `.glass`).
@available(iOS 26.0, *)
struct GlassButtonView: View {
  @ObservedObject var model: GlassModel

  var body: some View {
    let item = model.items.first
    let label = HStack(spacing: 8) {
      if let s = item?.symbol, !s.isEmpty { Image(systemName: s) }
      Text(model.title).fontWeight(.semibold).lineLimit(1)
    }
    .frame(maxWidth: .infinity, minHeight: 30)
    Group {
      if item?.prominent ?? true {
        Button { model.onTap(item?.id ?? "tap") } label: { label }
          .buttonStyle(.glassProminent)
          .tint(item?.destructive == true ? .red : brand)
      } else {
        Button { model.onTap(item?.id ?? "tap") } label: { label }
          .buttonStyle(.glass)
          .tint(item?.destructive == true ? .red : nil)
      }
    }
    .controlSize(.large)
    .disabled(!model.enabled)
    .accessibilityIdentifier("glass-\(item?.id ?? "button")")
    .frame(maxWidth: .infinity, maxHeight: .infinity)
  }
}
