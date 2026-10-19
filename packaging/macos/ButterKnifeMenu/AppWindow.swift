// The app window: ButterKnife's web UI in WebKit's own view, without browser chrome. There is no address bar, no back
// button and no back swipe, so nothing navigates away from a chat by accident; links to other sites open in the
// default browser; downloads (backups, chat exports) go through a save panel; file inputs get the system open panel;
// the microphone asks through the system prompt. Closing the window only hides it: the page (and anything running in
// it) stays alive in the menu bar app, and the Dock icon goes away until the window is shown again.
import AppKit
import WebKit

@MainActor
final class AppWindowController: NSObject, NSWindowDelegate, WKNavigationDelegate, WKUIDelegate, WKDownloadDelegate {
    private static let zoomKey = "ButterKnife.pageZoom"
    private static let zoomSteps: [CGFloat] = [0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0]

    let window: NSWindow
    private let webView: ButterKnifeWebView
    private var serverUrl: URL?
    private var titleObservation: NSKeyValueObservation?
    private var retries = 0

    override init() {
        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = .default() // keeps the theme and dictation choices (localStorage) between launches
        configuration.applicationNameForUserAgent = "ButterKnifeApp"
        webView = ButterKnifeWebView(frame: .zero, configuration: configuration)
        webView.allowsBackForwardNavigationGestures = false
        webView.allowsMagnification = false
        if #available(macOS 13.3, *) {
            webView.isInspectable = true // Safari → Develop menu, for debugging the page in the app
        }
        let zoom = UserDefaults.standard.double(forKey: AppWindowController.zoomKey)
        webView.pageZoom = zoom > 0 ? CGFloat(zoom) : 1.0

        window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1200, height: 820),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false)
        window.title = "ButterKnife"
        window.minSize = NSSize(width: 640, height: 480)
        window.isReleasedWhenClosed = false
        window.tabbingMode = .disallowed
        window.contentView = webView
        window.center()
        window.setFrameAutosaveName("ButterKnifeMainWindow")
        super.init()

        window.delegate = self
        webView.navigationDelegate = self
        webView.uiDelegate = self
        titleObservation = webView.observe(\.title, options: [.new]) { [weak self] view, _ in
            let title = view.title
            DispatchQueue.main.async {
                self?.window.title = (title?.isEmpty == false ? title! : "ButterKnife")
            }
        }
        showStarting()
    }

    var isVisible: Bool { window.isVisible }

    // MARK: Showing and hiding

    func show() {
        NSApp.setActivationPolicy(.regular) // Dock icon and app menus while the window is around
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    /// The close button and ⌘W hide the window: the page keeps running, and the app drops back to the menu bar.
    func windowShouldClose(_ sender: NSWindow) -> Bool {
        window.orderOut(nil)
        NSApp.setActivationPolicy(.accessory)
        return false
    }

    // MARK: Server

    /// The server reported its address: load the UI (the window has shown "Starting…" until now).
    func serverStarted(at url: URL) {
        let first = serverUrl == nil
        serverUrl = url
        if first {
            webView.load(URLRequest(url: url))
        }
    }

    /// The page the window shows before the server is up, or while it is unreachable.
    private func showStarting(message: String = "Starting ButterKnife…") {
        let html = """
            <!doctype html><html><head><meta charset="utf-8"><style>
            :root { color-scheme: light dark; }
            body { margin: 0; height: 100vh; display: flex; align-items: center; justify-content: center;
                   font: 15px -apple-system, system-ui, sans-serif; color: #6c757d; background: Canvas; }
            </style></head><body>\(message)</body></html>
            """
        webView.loadHTMLString(html, baseURL: nil)
    }

    // MARK: Menu commands

    /// Moves the page within the app (no reload) when Blazor is up; a plain load otherwise.
    func navigate(to path: String) {
        guard let base = serverUrl else { return }
        show()
        let script = "window.Blazor && Blazor.navigateTo ? Blazor.navigateTo('\(path)') : location.assign('\(path)')"
        if isOwn(webView.url) {
            webView.evaluateJavaScript(script, completionHandler: nil)
        } else if let url = URL(string: path, relativeTo: base) {
            webView.load(URLRequest(url: url))
        }
    }

    @objc func reloadPage(_ sender: Any?) {
        if isOwn(webView.url) {
            webView.reload()
        } else if let url = serverUrl {
            webView.load(URLRequest(url: url))
        }
    }

    @objc func zoomIn(_ sender: Any?) { setZoom(AppWindowController.zoomSteps.first { $0 > webView.pageZoom + 0.001 } ?? webView.pageZoom) }

    @objc func zoomOut(_ sender: Any?) { setZoom(AppWindowController.zoomSteps.last { $0 < webView.pageZoom - 0.001 } ?? webView.pageZoom) }

    @objc func actualSize(_ sender: Any?) { setZoom(1.0) }

    private func setZoom(_ zoom: CGFloat) {
        webView.pageZoom = zoom
        UserDefaults.standard.set(Double(zoom), forKey: AppWindowController.zoomKey)
    }

    /// The page the window is on, in the default browser (or the start page before anything has loaded).
    var currentUrl: URL? { isOwn(webView.url) ? webView.url : serverUrl }

    // MARK: Navigation: stay on ButterKnife, send everything else to the browser

    private func isOwn(_ url: URL?) -> Bool {
        guard let url = url, let server = serverUrl, url.scheme == "http" || url.scheme == "https" else { return false }
        let local: Set<String> = ["localhost", "127.0.0.1", "::1", "[::1]"]
        let sameHost = url.host == server.host || (local.contains(url.host ?? "") && local.contains(server.host ?? ""))
        return sameHost && (url.port ?? 80) == (server.port ?? 80)
    }

    func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction, decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
        if navigationAction.shouldPerformDownload {
            decisionHandler(.download) // <a download>: the backup and chat exports
            return
        }
        guard let url = navigationAction.request.url else {
            decisionHandler(.allow)
            return
        }
        let inPage = ["about", "blob", "data"].contains(url.scheme ?? "")
        let subframe = navigationAction.targetFrame.map { !$0.isMainFrame } ?? false
        if inPage || subframe || isOwn(url) {
            decisionHandler(.allow)
        } else {
            NSWorkspace.shared.open(url) // another site (a release page, GitHub, a link in a reply)
            decisionHandler(.cancel)
        }
    }

    func webView(_ webView: WKWebView, decidePolicyFor navigationResponse: WKNavigationResponse, decisionHandler: @escaping (WKNavigationResponsePolicy) -> Void) {
        decisionHandler(navigationResponse.canShowMIMEType ? .allow : .download)
    }

    /// target="_blank": our own pages open here, anything else in the browser. No second web view is ever made.
    func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration, for navigationAction: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
        if let url = navigationAction.request.url {
            if isOwn(url) {
                webView.load(navigationAction.request)
            } else {
                NSWorkspace.shared.open(url)
            }
        }
        return nil
    }

    func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) {
        retries = 0
    }

    /// The server is not answering (still starting, or restarting): show a note and try again shortly.
    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        guard let url = serverUrl, (error as NSError).code != NSURLErrorCancelled else { return }
        retries += 1
        showStarting(message: retries < 20 ? "Waiting for ButterKnife…" : "ButterKnife is not answering. It may have stopped; quit it from the menu bar and open it again.")
        if retries < 20 {
            DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in
                self?.webView.load(URLRequest(url: url))
            }
        }
    }

    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        reloadPage(nil)
    }

    // MARK: Downloads

    func webView(_ webView: WKWebView, navigationAction: WKNavigationAction, didBecome download: WKDownload) {
        download.delegate = self
    }

    func webView(_ webView: WKWebView, navigationResponse: WKNavigationResponse, didBecome download: WKDownload) {
        download.delegate = self
    }

    /// Asks where to save, starting in Downloads with the name the page suggested, like a browser set to ask.
    func download(_ download: WKDownload, decideDestinationUsing response: URLResponse, suggestedFilename: String, completionHandler: @escaping (URL?) -> Void) {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = suggestedFilename
        panel.directoryURL = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first
        panel.canCreateDirectories = true
        panel.beginSheetModal(for: window) { response in
            guard response == .OK, let url = panel.url else {
                completionHandler(nil)
                return
            }
            // The panel already asked about replacing; WebKit refuses to overwrite, so clear the way.
            try? FileManager.default.removeItem(at: url)
            completionHandler(url)
        }
    }

    func download(_ download: WKDownload, didFailWithError error: Error, resumeData: Data?) {
        let alert = NSAlert()
        alert.messageText = "The download failed"
        alert.informativeText = error.localizedDescription
        alert.beginSheetModal(for: window)
    }

    // MARK: File pickers and the microphone

    func webView(_ webView: WKWebView, runOpenPanelWith parameters: WKOpenPanelParameters, initiatedByFrame frame: WKFrameInfo, completionHandler: @escaping ([URL]?) -> Void) {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = parameters.allowsMultipleSelection
        panel.canChooseDirectories = parameters.allowsDirectories
        panel.canChooseFiles = true
        panel.beginSheetModal(for: window) { response in
            completionHandler(response == .OK ? panel.urls : nil)
        }
    }

    /// Dictation records through the page; our own pages may use the microphone (macOS still asks the user once).
    func webView(_ webView: WKWebView, requestMediaCapturePermissionFor origin: WKSecurityOrigin, initiatedByFrame frame: WKFrameInfo, type: WKMediaCaptureType, decisionHandler: @escaping (WKPermissionDecision) -> Void) {
        let ownOrigin = serverUrl.map { origin.host == $0.host || origin.host == "localhost" } ?? false
        decisionHandler(ownOrigin && type == .microphone ? .grant : .deny)
    }
}

/// The web view without Back and Forward in its context menu (nothing in the app should navigate history).
final class ButterKnifeWebView: WKWebView {
    private static let hidden: Set<String> = [
        "WKMenuItemIdentifierGoBack",
        "WKMenuItemIdentifierGoForward",
        "WKMenuItemIdentifierOpenLinkInNewWindow",
        "WKMenuItemIdentifierOpenImageInNewWindow",
        "WKMenuItemIdentifierOpenMediaInNewWindow",
        "WKMenuItemIdentifierOpenFrameInNewWindow",
    ]

    override func willOpenMenu(_ menu: NSMenu, with event: NSEvent) {
        for item in menu.items where ButterKnifeWebView.hidden.contains(item.identifier?.rawValue ?? "") {
            menu.removeItem(item)
        }
        super.willOpenMenu(menu, with: event)
    }

    /// The side buttons on some mice mean Back and Forward; swallow them.
    override func otherMouseUp(with event: NSEvent) {
        if event.buttonNumber == 3 || event.buttonNumber == 4 {
            return
        }
        super.otherMouseUp(with: event)
    }
}

/// The app's menu bar menus, shown while the window is open (the app is a plain menu bar item otherwise).
@MainActor
enum AppMenus {
    static func build(target: AppDelegate, window: AppWindowController) -> NSMenu {
        let main = NSMenu()

        let app = submenu(main, "ButterKnife")
        app.addItem(withTitle: "About ButterKnife", action: #selector(NSApplication.orderFrontStandardAboutPanel(_:)), keyEquivalent: "")
        app.addItem(.separator())
        app.addItem(item("Settings…", #selector(AppDelegate.openSettings), ",", target))
        app.addItem(.separator())
        app.addItem(withTitle: "Hide ButterKnife", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h")
        let others = app.addItem(withTitle: "Hide Others", action: #selector(NSApplication.hideOtherApplications(_:)), keyEquivalent: "h")
        others.keyEquivalentModifierMask = [.command, .option]
        app.addItem(withTitle: "Show All", action: #selector(NSApplication.unhideAllApplications(_:)), keyEquivalent: "")
        app.addItem(.separator())
        app.addItem(withTitle: "Quit ButterKnife", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")

        let file = submenu(main, "File")
        file.addItem(item("New Chat", #selector(AppDelegate.newChat), "n", target))
        let code = item("New Code Session", #selector(AppDelegate.newCodeSession), "n", target)
        code.keyEquivalentModifierMask = [.command, .shift]
        file.addItem(code)
        file.addItem(.separator())
        let browser = item("Open in Browser", #selector(AppDelegate.openBrowser), "o", target)
        browser.keyEquivalentModifierMask = [.command, .option]
        file.addItem(browser)
        file.addItem(.separator())
        file.addItem(withTitle: "Close Window", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")

        // The web view answers these through the responder chain, like any text view.
        let edit = submenu(main, "Edit")
        edit.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
        let redo = edit.addItem(withTitle: "Redo", action: Selector(("redo:")), keyEquivalent: "z")
        redo.keyEquivalentModifierMask = [.command, .shift]
        edit.addItem(.separator())
        edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")

        let view = submenu(main, "View")
        view.addItem(item("Reload", #selector(AppWindowController.reloadPage(_:)), "r", window))
        view.addItem(.separator())
        view.addItem(item("Actual Size", #selector(AppWindowController.actualSize(_:)), "0", window))
        view.addItem(item("Zoom In", #selector(AppWindowController.zoomIn(_:)), "=", window))
        view.addItem(item("Zoom Out", #selector(AppWindowController.zoomOut(_:)), "-", window))
        view.addItem(.separator())
        let fullScreen = view.addItem(withTitle: "Enter Full Screen", action: #selector(NSWindow.toggleFullScreen(_:)), keyEquivalent: "f")
        fullScreen.keyEquivalentModifierMask = [.command, .control]

        let windows = submenu(main, "Window")
        windows.addItem(withTitle: "Minimize", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        windows.addItem(withTitle: "Zoom", action: #selector(NSWindow.performZoom(_:)), keyEquivalent: "")
        windows.addItem(.separator())
        windows.addItem(withTitle: "Bring All to Front", action: #selector(NSApplication.arrangeInFront(_:)), keyEquivalent: "")
        NSApp.windowsMenu = windows

        return main
    }

    private static func submenu(_ main: NSMenu, _ title: String) -> NSMenu {
        let holder = main.addItem(withTitle: title, action: nil, keyEquivalent: "")
        let menu = NSMenu(title: title)
        holder.submenu = menu
        return menu
    }

    private static func item(_ title: String, _ action: Selector, _ key: String, _ target: AnyObject) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: key)
        item.target = target
        return item
    }
}
