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
    guard let registrar = engineBridge.pluginRegistry.registrar(forPlugin: "RemotePCGlass") else { return }
    GlassBridge.register(with: registrar)
  }
}

// MARK: - Liquid Glass (iOS 26+) ------------------------------------------------------------
//
// The Dart side asks `isSupported` and only then creates the platform view. On iOS 18–25 the Swift
// glass APIs are never called: every use is behind `if #available(iOS 26.0, *)`.

enum GlassBridge {
  static func register(with registrar: FlutterPluginRegistrar) {
    let channel = FlutterMethodChannel(name: "ai.remotepc/glass", binaryMessenger: registrar.messenger())
    channel.setMethodCallHandler { call, result in
      switch call.method {
      case "isSupported":
        if #available(iOS 26.0, *) { result(true) } else { result(false) }
      default:
        result(FlutterMethodNotImplemented)
      }
    }
    registrar.register(GlassToolbarFactory(messenger: registrar.messenger()), withId: "ai.remotepc/glass-toolbar")
  }
}

struct GlassItem: Identifiable, Equatable {
  let id: String
  let symbol: String
  let label: String
  let active: Bool

  static func parse(_ args: Any?) -> [GlassItem] {
    guard let dict = args as? [String: Any], let actions = dict["actions"] as? [[String: Any]] else { return [] }
    return actions.compactMap { a in
      guard let id = a["id"] as? String else { return nil }
      return GlassItem(id: id, symbol: a["symbol"] as? String ?? "circle", label: a["label"] as? String ?? id, active: a["active"] as? Bool ?? false)
    }
  }
}

final class GlassToolbarModel: ObservableObject {
  @Published var items: [GlassItem]
  var onTap: (String) -> Void = { _ in }
  init(items: [GlassItem]) { self.items = items }
}

final class GlassToolbarFactory: NSObject, FlutterPlatformViewFactory {
  private let messenger: FlutterBinaryMessenger
  init(messenger: FlutterBinaryMessenger) { self.messenger = messenger }

  func createArgsCodec() -> FlutterMessageCodec & NSObjectProtocol { FlutterStandardMessageCodec.sharedInstance() }

  func create(withFrame frame: CGRect, viewIdentifier viewId: Int64, arguments args: Any?) -> FlutterPlatformView {
    GlassToolbarPlatformView(frame: frame, viewId: viewId, args: args, messenger: messenger)
  }
}

final class GlassToolbarPlatformView: NSObject, FlutterPlatformView {
  private let host: UIView
  private let model: GlassToolbarModel
  private let channel: FlutterMethodChannel

  init(frame: CGRect, viewId: Int64, args: Any?, messenger: FlutterBinaryMessenger) {
    model = GlassToolbarModel(items: GlassItem.parse(args))
    channel = FlutterMethodChannel(name: "ai.remotepc/glass-toolbar/\(viewId)", binaryMessenger: messenger)
    if #available(iOS 26.0, *) {
      let controller = UIHostingController(rootView: GlassToolbarView(model: model))
      controller.view.backgroundColor = .clear
      controller.view.frame = frame
      host = controller.view
    } else {
      // Never reached: Dart only creates this view when isSupported == true.
      host = UIView(frame: frame)
    }
    super.init()
    model.onTap = { [weak self] id in self?.channel.invokeMethod("tap", arguments: id) }
    channel.setMethodCallHandler { [weak self] call, result in
      if call.method == "update" { self?.model.items = GlassItem.parse(call.arguments) }
      result(nil)
    }
  }

  func view() -> UIView { host }
}

@available(iOS 26.0, *)
struct GlassToolbarView: View {
  @ObservedObject var model: GlassToolbarModel
  @Namespace private var ns

  var body: some View {
    GlassEffectContainer(spacing: 8) {
      ScrollView(.horizontal, showsIndicators: false) {
        HStack(spacing: 6) {
          ForEach(model.items) { item in
            Button {
              model.onTap(item.id)
            } label: {
              Image(systemName: item.symbol)
                .font(.system(size: 17, weight: .semibold))
                .frame(width: 44, height: 44)
            }
            .accessibilityLabel(item.label)
            .buttonStyle(.plain)
            .glassEffect(item.active ? .regular.tint(.accentColor).interactive() : .regular.interactive(), in: .circle)
            .glassEffectID(item.id, in: ns)
          }
        }
        .padding(.horizontal, 10)
      }
    }
    .frame(maxWidth: .infinity, maxHeight: .infinity)
  }
}
