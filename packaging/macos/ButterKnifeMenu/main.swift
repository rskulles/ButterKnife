// ButterKnife menu bar helper (macOS). The .app's main executable: starts the ButterKnife server that sits beside
// it in Contents/MacOS, shows a menu bar item (Open, Copy address for phone, Quit), opens the browser once the
// server reports its address, and stops the server when quitting or when the server exits on its own (Settings →
// Quit in the web UI). Built by tools/make-macos-app.sh with swiftc; no Xcode project.
import AppKit
import Foundation

final class AppDelegate: NSObject, NSApplicationDelegate {
    private var statusItem: NSStatusItem!
    private var openItem: NSMenuItem!
    private var copyItem: NSMenuItem!
    private var server: Process?
    private var localUrl = URL(string: "http://localhost:5175/")!
    private var lanUrl: URL?
    private var browserOpened = false
    private var quitting = false
    private var log: FileHandle?
    private var buffer = Data()
    private var signalSources: [DispatchSourceSignal] = []

    func applicationDidFinishLaunching(_ notification: Notification) {
        buildMenu()
        openLog()
        installSignalHandlers()
        startServer()
    }

    /// `kill`, logout and the like send SIGTERM/SIGINT; route them through the normal quit so the server is stopped too.
    private func installSignalHandlers() {
        for sig in [SIGTERM, SIGINT, SIGHUP] {
            signal(sig, SIG_IGN)
            let source = DispatchSource.makeSignalSource(signal: sig, queue: .main)
            source.setEventHandler { NSApp.terminate(nil) }
            source.resume()
            signalSources.append(source)
        }
    }

    private func buildMenu() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let image = Bundle.main.image(forResource: "MenuIcon") {
            image.isTemplate = true
            statusItem.button?.image = image
        } else {
            statusItem.button?.title = "BK"
        }
        statusItem.button?.toolTip = "ButterKnife (starting…)"

        let menu = NSMenu()
        openItem = NSMenuItem(title: "Open ButterKnife", action: #selector(openBrowser), keyEquivalent: "o")
        openItem.target = self
        copyItem = NSMenuItem(title: "Copy address for phone", action: #selector(copyAddress), keyEquivalent: "")
        copyItem.target = self
        copyItem.isEnabled = false
        let quitItem = NSMenuItem(title: "Quit ButterKnife", action: #selector(quit), keyEquivalent: "q")
        quitItem.target = self
        menu.addItem(openItem)
        menu.addItem(copyItem)
        menu.addItem(.separator())
        menu.addItem(quitItem)
        menu.autoenablesItems = false
        statusItem.menu = menu
    }

    private func openLog() {
        let logs = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/ButterKnife")
        try? FileManager.default.createDirectory(at: logs, withIntermediateDirectories: true)
        let file = logs.appendingPathComponent("server.log")
        if !FileManager.default.fileExists(atPath: file.path) {
            FileManager.default.createFile(atPath: file.path, contents: nil)
        }
        log = try? FileHandle(forWritingTo: file)
        log?.seekToEndOfFile()
    }

    private func startServer() {
        let process = Process()
        process.executableURL = Bundle.main.bundleURL.appendingPathComponent("Contents/MacOS/ButterKnife")
        var environment = ProcessInfo.processInfo.environment
        environment["BUTTERKNIFE_NO_BROWSER"] = "1"
        // The server stops itself if this helper dies without the chance to stop it (see DesktopLauncher).
        environment["BUTTERKNIFE_PARENT_PID"] = String(ProcessInfo.processInfo.processIdentifier)
        process.environment = environment

        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = pipe
        pipe.fileHandleForReading.readabilityHandler = { [weak self] handle in
            let data = handle.availableData
            if data.isEmpty { return }
            DispatchQueue.main.async { self?.consume(data) }
        }
        process.terminationHandler = { [weak self] _ in
            DispatchQueue.main.async {
                guard let self = self else { return }
                if !self.quitting {
                    // The server stopped on its own (Settings → Quit, or a crash): nothing left to manage.
                    NSApp.terminate(nil)
                }
            }
        }

        do {
            try process.run()
            server = process
        } catch {
            let alert = NSAlert()
            alert.messageText = "ButterKnife could not start"
            alert.informativeText = error.localizedDescription
            alert.runModal()
            NSApp.terminate(nil)
        }
    }

    /// Server output goes to the log file; the "running at" line tells us the addresses.
    private func consume(_ data: Data) {
        log?.write(data)
        buffer.append(data)
        while let newline = buffer.firstIndex(of: 0x0A) {
            let line = String(decoding: buffer[buffer.startIndex..<newline], as: UTF8.self)
            buffer.removeSubrange(buffer.startIndex...newline)
            parse(line)
        }
    }

    private func parse(_ line: String) {
        guard let range = line.range(of: "ButterKnife is running at ") else { return }
        var rest = line[range.upperBound...]
        if let space = rest.firstIndex(of: " ") { rest = rest[rest.startIndex..<space] }
        if let url = URL(string: String(rest)) { localUrl = url }
        if let lanRange = line.range(of: "(on your network: "), let end = line[lanRange.upperBound...].firstIndex(of: ")") {
            lanUrl = URL(string: String(line[lanRange.upperBound..<end]))
        }
        copyItem.isEnabled = lanUrl != nil
        statusItem.button?.toolTip = lanUrl.map { "ButterKnife at \(localUrl) (network: \($0))" } ?? "ButterKnife at \(localUrl)"
        if !browserOpened && ProcessInfo.processInfo.environment["BUTTERKNIFE_NO_BROWSER"] == nil {
            browserOpened = true
            NSWorkspace.shared.open(localUrl)
        }
    }

    @objc private func openBrowser() { NSWorkspace.shared.open(localUrl) }

    @objc private func copyAddress() {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString((lanUrl ?? localUrl).absoluteString, forType: .string)
    }

    @objc private func quit() { NSApp.terminate(nil) }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        quitting = true
        guard let server = server, server.isRunning else { return .terminateNow }
        // SIGTERM lets the .NET host shut down cleanly (flushes SQLite). Wait for it here, pumping the run loop so the
        // termination is noticed, and force it after a few seconds; then let the app exit.
        server.terminate()
        let deadline = Date().addingTimeInterval(5)
        while server.isRunning && Date() < deadline {
            RunLoop.current.run(mode: .default, before: Date().addingTimeInterval(0.1))
        }
        if server.isRunning { kill(server.processIdentifier, SIGKILL) }
        return .terminateNow
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
