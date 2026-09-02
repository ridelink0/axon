// Axon host for macOS.
//
// Same line-delimited JSON protocol as the Windows host, so the Node driver
// above it is platform-agnostic. Reads and drives apps through the macOS
// Accessibility API (AXUIElement) rather than pixels, matching the Windows
// side's tree-first design.
//
// Compiled locally by the swiftc that comes with the Xcode Command Line Tools.
// Shipped as source, never as a binary.
//
// NOTE: this port was written on a Windows machine and has not been compiled or
// executed on macOS. Run `node server/build.mjs --self-test` on a Mac before
// relying on it; a compile failure surfaces the compiler's own output.

import AppKit
import ApplicationServices
import CoreGraphics
import Foundation

// Marks every event Axon posts. Real hardware input carries 0 here, so the
// presence tracker can tell the user apart from Axon - the macOS counterpart of
// the injected-event flag Windows sets in the kernel.
let AXON_EVENT_MARKER: Int64 = 0x41584F4E  // "AXON"

// MARK: - JSON plumbing

func emit(_ obj: [String: Any]) {
    guard let data = try? JSONSerialization.data(withJSONObject: obj, options: []),
          let line = String(data: data, encoding: .utf8) else { return }
    print(line)
    fflush(stdout)
}

struct AxonError: Error {
    let code: String
    let message: String
    let hint: String?
    init(_ code: String, _ message: String, _ hint: String? = nil) {
        self.code = code; self.message = message; self.hint = hint
    }
}

func str(_ v: Any?) -> String? { return v as? String }
func int(_ v: Any?, _ fallback: Int) -> Int {
    if let n = v as? Int { return n }
    if let n = v as? Double { return Int(n) }
    if let s = v as? String, let n = Int(s) { return n }
    return fallback
}
func bool(_ v: Any?, _ fallback: Bool) -> Bool { return (v as? Bool) ?? fallback }

// MARK: - Presence

final class Presence {
    static let shared = Presence()
    private var tap: CFMachPort?
    private var lastAny = Date()
    private var lastCommit = Date()
    private var lastKind = "none"
    private var realEvents: Int = 0
    private var injectedEvents: Int = 0
    private var commitWindowPid: pid_t = 0
    private let lock = NSLock()
    private(set) var monitoring = false

    func start() {
        let mask: CGEventMask =
            (1 << CGEventType.mouseMoved.rawValue) |
            (1 << CGEventType.leftMouseDown.rawValue) |
            (1 << CGEventType.rightMouseDown.rawValue) |
            (1 << CGEventType.scrollWheel.rawValue) |
            (1 << CGEventType.keyDown.rawValue) |
            (1 << CGEventType.flagsChanged.rawValue)

        // Listen-only: this tap never modifies or swallows input, it only
        // observes, so it cannot interfere with the user's typing.
        guard let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .listenOnly,
            eventsOfInterest: mask,
            callback: { _, type, event, _ in
                Presence.shared.observe(type: type, event: event)
                return Unmanaged.passUnretained(event)
            },
            userInfo: nil
        ) else { return }

        self.tap = tap
        let src = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        CFRunLoopAddSource(CFRunLoopGetMain(), src, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        monitoring = true
    }

    fileprivate func observe(type: CGEventType, event: CGEvent) {
        let marker = event.getIntegerValueField(.eventSourceUserData)
        lock.lock()
        defer { lock.unlock() }
        if marker == AXON_EVENT_MARKER {
            injectedEvents += 1
            return
        }
        let now = Date()
        lastAny = now
        realEvents += 1
        lastKind = (type == .keyDown || type == .flagsChanged) ? "keyboard" : "mouse"
        if type != .mouseMoved {
            lastCommit = now
            commitWindowPid = NSWorkspace.shared.frontmostApplication?.processIdentifier ?? 0
        }
    }

    var idleMs: Int {
        lock.lock(); defer { lock.unlock() }
        return Int(Date().timeIntervalSince(lastAny) * 1000)
    }
    var commitIdleMs: Int {
        lock.lock(); defer { lock.unlock() }
        return Int(Date().timeIntervalSince(lastCommit) * 1000)
    }
    var userPid: pid_t {
        lock.lock(); defer { lock.unlock() }
        return commitWindowPid
    }

    func active(_ thresholdMs: Int) -> Bool { return monitoring && idleMs < thresholdMs }
    func busy(_ thresholdMs: Int) -> Bool { return monitoring && commitIdleMs < thresholdMs }

    func waitForQuiet(idleMs wanted: Int, budgetMs budget: Int) -> Bool {
        if !monitoring { return true }
        var waited = 0
        while waited < budget {
            if idleMs >= wanted { return true }
            Thread.sleep(forTimeInterval: 0.1)
            waited += 100
        }
        return idleMs >= wanted
    }

    func report() -> [String: Any] {
        lock.lock()
        let real = realEvents, inj = injectedEvents, kind = lastKind
        lock.unlock()
        return [
            "monitoring": monitoring,
            "idle_ms": idleMs,
            "commit_idle_ms": commitIdleMs,
            "last_input": kind,
            "real_events": real,
            "injected_events": inj,
            "user_window": Int(userPid),
        ]
    }
}

// MARK: - Accessibility helpers

func axAttr(_ el: AXUIElement, _ name: String) -> CFTypeRef? {
    var value: CFTypeRef?
    let err = AXUIElementCopyAttributeValue(el, name as CFString, &value)
    return err == .success ? value : nil
}

func axString(_ el: AXUIElement, _ name: String) -> String? {
    return axAttr(el, name) as? String
}

func axChildren(_ el: AXUIElement) -> [AXUIElement] {
    guard let raw = axAttr(el, kAXChildrenAttribute as String) else { return [] }
    return (raw as? [AXUIElement]) ?? []
}

func axActions(_ el: AXUIElement) -> [String] {
    var names: CFArray?
    guard AXUIElementCopyActionNames(el, &names) == .success else { return [] }
    return (names as? [String]) ?? []
}

func axRect(_ el: AXUIElement) -> [Int]? {
    guard let posRaw = axAttr(el, kAXPositionAttribute as String),
          let sizeRaw = axAttr(el, kAXSizeAttribute as String) else { return nil }
    // These attributes are normally AXValue, but a misbehaving provider can
    // return something else. A force-cast there would crash the whole host and
    // take the session down, so the type is checked before casting.
    guard CFGetTypeID(posRaw) == AXValueGetTypeID(),
          CFGetTypeID(sizeRaw) == AXValueGetTypeID() else { return nil }
    var origin = CGPoint.zero
    var size = CGSize.zero
    AXValueGetValue(posRaw as! AXValue, .cgPoint, &origin)
    AXValueGetValue(sizeRaw as! AXValue, .cgSize, &size)
    if size.width <= 0 || size.height <= 0 { return nil }
    return [Int(origin.x), Int(origin.y), Int(size.width), Int(size.height)]
}

func axValueText(_ el: AXUIElement) -> String? {
    if let s = axAttr(el, kAXValueAttribute as String) as? String, !s.isEmpty { return s }
    return nil
}

// MARK: - Session state

final class Snapshot {
    var elements: [AXUIElement] = []
    var pid: pid_t = 0
    var title: String = ""
    var taken = Date()
}

var snapshots: [String: Snapshot] = [:]
var snapSeq = 0
let maxSnapshots = 8

var idleThresholdMs = 1200
var waitBudgetMs = 6000
var defaultMode = "share"

func modeOf(_ a: [String: Any]) -> String {
    return str(a["mode"]) ?? defaultMode
}

func guardSameWindow(_ a: [String: Any], _ targetPid: pid_t) throws {
    // take and exclusive both mean "go now", matching the Windows host.
    let m = modeOf(a)
    if m == "take" || m == "exclusive" { return }
    if !Presence.shared.monitoring || targetPid == 0 { return }
    if !Presence.shared.busy(idleThresholdMs) { return }
    if Presence.shared.userPid != targetPid { return }
    if NSWorkspace.shared.frontmostApplication?.processIdentifier != targetPid { return }
    throw AxonError("user_in_window", "The user is working in that app right now.",
                    "Work somewhere else, wait for them to stop, or pass mode:\"take\".")
}

func guardDisturb(_ a: [String: Any]) throws -> Int {
    let mode = modeOf(a)
    if mode == "take" || mode == "exclusive" { return -1 }
    if !Presence.shared.monitoring { return -1 }
    if !Presence.shared.active(idleThresholdMs) { return -1 }
    if mode == "yield" {
        throw AxonError("user_busy",
                        "The user is using the mouse or keyboard, and this step needs the cursor or the foreground.",
                        "Reading and AX actions still work while they are busy.")
    }
    let start = Date()
    if !Presence.shared.waitForQuiet(idleMs: idleThresholdMs, budgetMs: waitBudgetMs) {
        throw AxonError("user_busy",
                        "Waited \(waitBudgetMs)ms but the user is still active.",
                        "Reading and AX actions still work. Retry later, or pass mode:\"take\".")
    }
    return Int(Date().timeIntervalSince(start) * 1000)
}

// MARK: - Ops

func trusted() -> Bool {
    return AXIsProcessTrusted()
}

func requireTrust() throws {
    if !trusted() {
        throw AxonError("accessibility_denied",
                        "Axon does not have Accessibility permission.",
                        "Grant it in System Settings > Privacy & Security > Accessibility for the terminal or app running Claude Code, then restart it.")
    }
}

func opListApps(_ a: [String: Any]) throws -> [String: Any] {
    try requireTrust()
    var out: [[String: Any]] = []
    let front = NSWorkspace.shared.frontmostApplication?.processIdentifier ?? 0
    for app in NSWorkspace.shared.runningApplications {
        guard app.activationPolicy == .regular, let name = app.localizedName else { continue }
        let pid = app.processIdentifier
        let axApp = AXUIElementCreateApplication(pid)
        guard let windows = axAttr(axApp, kAXWindowsAttribute as String) as? [AXUIElement] else { continue }
        // Handles are (pid * 1000 + index-within-that-app). resolveWindow must
        // number them exactly the same way or a handle from here resolves to a
        // different window, or to none.
        for (winIndex, win) in windows.enumerated() {
            let title = axString(win, kAXTitleAttribute as String) ?? ""
            let minimized = (axAttr(win, kAXMinimizedAttribute as String) as? Bool) ?? false
            out.append([
                "hwnd": Int(pid) * 1000 + winIndex,
                "pid": Int(pid),
                "title": title,
                "class": axString(win, kAXRoleAttribute as String) ?? "AXWindow",
                "process": name,
                "path": app.bundleURL?.path as Any,
                "rect": axRect(win) as Any,
                "minimized": minimized,
                "foreground": pid == front,
            ])
        }
    }
    return ["windows": out, "dpi_mode": "native", "platform": "darwin"]
}

func resolveWindow(_ a: [String: Any]) throws -> (AXUIElement, pid_t, String) {
    try requireTrust()
    let wantHandle = int(a["hwnd"], -1)
    let wantTitle = str(a["title"])
    var partial: (AXUIElement, pid_t, String)?
    for app in NSWorkspace.shared.runningApplications {
        guard app.activationPolicy == .regular, app.localizedName != nil else { continue }
        let pid = app.processIdentifier
        let axApp = AXUIElementCreateApplication(pid)
        guard let windows = axAttr(axApp, kAXWindowsAttribute as String) as? [AXUIElement] else { continue }
        for (winIndex, win) in windows.enumerated() {
            let handle = Int(pid) * 1000 + winIndex
            let title = axString(win, kAXTitleAttribute as String) ?? ""
            if wantHandle >= 0 && handle == wantHandle { return (win, pid, title) }
            if let t = wantTitle {
                if title == t { return (win, pid, title) }
                if partial == nil && title.lowercased().contains(t.lowercased()) {
                    partial = (win, pid, title)
                }
            }
        }
    }
    if let p = partial { return p }
    throw AxonError("window_not_found", "No window matched hwnd/title.",
                    "Call list_apps and pass an hwnd from its result.")
}

let interestingActions: Set<String> = ["AXPress", "AXIncrement", "AXDecrement", "AXShowMenu", "AXPick", "AXConfirm"]

func opSnapshot(_ a: [String: Any]) throws -> [String: Any] {
    let (win, pid, title) = try resolveWindow(a)
    let maxNodes = int(a["max_nodes"], 400)
    let maxDepth = int(a["max_depth"], 14)
    let interactiveOnly = bool(a["interactive_only"], false)

    var nodes: [[String: Any]] = []
    var elements: [AXUIElement] = []
    var truncated = false

    var stack: [(AXUIElement, Int)] = [(win, 0)]
    while !stack.isEmpty {
        if nodes.count >= maxNodes { truncated = true; break }
        let (el, depth) = stack.removeLast()

        let rect = axRect(el)
        let actions = axActions(el)
        let role = axString(el, kAXRoleAttribute as String) ?? "AXUnknown"
        let name = axString(el, kAXTitleAttribute as String)
            ?? axString(el, kAXDescriptionAttribute as String)
        let interactive = actions.contains { interestingActions.contains($0) }

        var include = true
        if interactiveOnly && depth > 0 && !interactive { include = false }
        if rect == nil && depth > 0 { include = false }

        if include {
            var node: [String: Any] = ["i": nodes.count, "role": role, "depth": depth]
            if let n = name, !n.isEmpty { node["name"] = n }
            if let r = rect { node["rect"] = r }
            if !actions.isEmpty { node["patterns"] = actions.map { $0.replacingOccurrences(of: "AX", with: "") } }
            if let t = axValueText(el) {
                node["text"] = t.count > 4000 ? String(t.prefix(4000)) + "...[truncated]" : t
            }
            var state: [String: Any] = [:]
            if let enabled = axAttr(el, kAXEnabledAttribute as String) as? Bool, !enabled { state["disabled"] = true }
            if let focused = axAttr(el, kAXFocusedAttribute as String) as? Bool, focused { state["focused"] = true }
            if !state.isEmpty { node["state"] = state }
            nodes.append(node)
            elements.append(el)
        }

        if depth < maxDepth {
            let kids = axChildren(el)
            for kid in kids.reversed() { stack.append((kid, depth + 1)) }
        }
    }

    // A poll (wait_for) reads without keeping, so it neither bumps the
    // sequence nor evicts a snapshot the model is still acting on.
    let register = bool(a["register"], true)
    var sid: String? = nil
    if register {
        snapSeq += 1
        sid = "s\(snapSeq)"
        let snap = Snapshot()
        snap.elements = elements
        snap.pid = pid
        snap.title = title
        snapshots[sid!] = snap
    }
    while snapshots.count > maxSnapshots {
        if let oldest = snapshots.min(by: { $0.value.taken < $1.value.taken })?.key {
            snapshots.removeValue(forKey: oldest)
        } else { break }
    }

    return [
        "snapshot_id": sid as Any,
        "hwnd": int(a["hwnd"], Int(pid) * 1000),
        "title": title,
        "rect": axRect(win) as Any,
        "node_count": nodes.count,
        "truncated": truncated,
        "nodes": nodes,
    ]
}

// Selector targeting, so the macOS surface matches the Windows one instead of
// forcing every caller to take a snapshot first.
func findBySelector(_ a: [String: Any]) throws -> (AXUIElement, pid_t) {
    guard let sel = a["selector"] as? [String: Any] else {
        throw AxonError("bad_selector", "Selector must be an object.", nil)
    }
    let wantName = str(sel["name"])
    let wantRole = str(sel["role"])
    let wantId = str(sel["automation_id"])
    if wantName == nil && wantRole == nil && wantId == nil {
        throw AxonError("bad_selector", "Selector needs at least one of automation_id, name, or role.", nil)
    }
    let (win, pid, _) = try resolveWindow(a)

    var stack: [AXUIElement] = [win]
    var seen = 0
    while !stack.isEmpty && seen < 4000 {
        let el = stack.removeLast()
        seen += 1
        let role = axString(el, kAXRoleAttribute as String) ?? ""
        let name = axString(el, kAXTitleAttribute as String)
            ?? axString(el, kAXDescriptionAttribute as String)
        let ident = axString(el, kAXIdentifierAttribute as String)
        var ok = true
        if let w = wantName, name != w { ok = false }
        if let w = wantRole, role != w && role != "AX" + w { ok = false }
        if let w = wantId, ident != w { ok = false }
        if ok { return (el, pid) }
        for kid in axChildren(el) { stack.append(kid) }
    }
    throw AxonError("element_not_found", "No element matched the selector.",
                    "Take a snapshot to see what the window actually exposes.")
}

// One entry point for every way an action can name its target.
func resolveTarget(_ a: [String: Any]) throws -> (AXUIElement, pid_t) {
    if a["index"] != nil { return try snapshotElement(a) }
    if a["selector"] != nil { return try findBySelector(a) }
    throw AxonError("no_target", "Provide index or selector.",
                    "Prefer an index from a snapshot.")
}

func snapshotElement(_ a: [String: Any]) throws -> (AXUIElement, pid_t) {
    var sid = str(a["snapshot_id"]) ?? ""
    if sid.isEmpty {
        if snapSeq == 0 {
            throw AxonError("no_snapshot", "No snapshot has been taken yet.",
                            "Call snapshot on the target window first.")
        }
        sid = "s\(snapSeq)"
    }
    guard let snap = snapshots[sid] else {
        throw AxonError("snapshot_expired", "Snapshot \(sid) is no longer held.", "Take a fresh snapshot.")
    }
    let idx = int(a["index"], -1)
    guard idx >= 0 && idx < snap.elements.count else {
        throw AxonError("index_out_of_range",
                        "Index \(idx) is outside snapshot \(sid) (0..\(snap.elements.count - 1)).",
                        "Re-read the snapshot listing.")
    }
    let el = snap.elements[idx]
    if axAttr(el, kAXRoleAttribute as String) == nil {
        throw AxonError("element_stale", "Element \(idx) from \(sid) no longer exists.",
                        "The UI changed. Take a fresh snapshot.")
    }
    return (el, snap.pid)
}

func markedEvent(_ e: CGEvent?) -> CGEvent? {
    e?.setIntegerValueField(.eventSourceUserData, value: AXON_EVENT_MARKER)
    return e
}

// What the element looks like now the action has landed, so the caller does not
// need a second read just to confirm - the Windows host does the same.
func nowState(_ el: AXUIElement) -> [String: Any]? {
    var now: [String: Any] = [:]
    if let v = axAttr(el, kAXValueAttribute as String) {
        if let s = v as? String, !s.isEmpty { now["text"] = s.count > 200 ? String(s.prefix(200)) + "..." : s }
        else if let b = v as? Bool { now["value"] = b }
        else if let n = v as? Int { now["value"] = n }
    }
    if let enabled = axAttr(el, kAXEnabledAttribute as String) as? Bool, !enabled { now["disabled"] = true }
    return now.isEmpty ? nil : now
}

func opClick(_ a: [String: Any]) throws -> [String: Any] {
    var res: [String: Any] = [:]
    let (el, pid) = try resolveTarget(a)
    try guardSameWindow(a, pid)

    let forcePhysical = bool(a["physical"], false)
    let actions = axActions(el)

    // AXPress is the counterpart of the Windows invoke pattern: no cursor
    // movement, nothing the user can feel.
    if !forcePhysical && actions.contains("AXPress") {
        if AXUIElementPerformAction(el, "AXPress" as CFString) == .success {
            res["method"] = "ax_press"
            Thread.sleep(forTimeInterval: 0.04)
            if let now = nowState(el) { res["now"] = now }
            return res
        }
    }

    guard let rect = axRect(el) else {
        throw AxonError("no_click_point", "Element has no on-screen rectangle.", "Scroll it into view first.")
    }
    let waited = try guardDisturb(a)
    if waited > 0 { res["waited_for_user_ms"] = waited }

    let point = CGPoint(x: Double(rect[0] + rect[2] / 2), y: Double(rect[1] + rect[3] / 2))
    let saved = CGEvent(source: nil)?.location

    let src = CGEventSource(stateID: .hidSystemState)
    markedEvent(CGEvent(mouseEventSource: src, mouseType: .mouseMoved, mouseCursorPosition: point, mouseButton: .left))?.post(tap: .cghidEventTap)
    markedEvent(CGEvent(mouseEventSource: src, mouseType: .leftMouseDown, mouseCursorPosition: point, mouseButton: .left))?.post(tap: .cghidEventTap)
    markedEvent(CGEvent(mouseEventSource: src, mouseType: .leftMouseUp, mouseCursorPosition: point, mouseButton: .left))?.post(tap: .cghidEventTap)

    // take and exclusive both mean "drive it"; only the courtesy modes put the
    // pointer back where the user left it.
    let m = modeOf(a)
    if m != "take" && m != "exclusive", let back = saved {
        Thread.sleep(forTimeInterval: 0.02)
        markedEvent(CGEvent(mouseEventSource: src, mouseType: .mouseMoved, mouseCursorPosition: back, mouseButton: .left))?.post(tap: .cghidEventTap)
        res["cursor_restored"] = true
    }
    res["method"] = "physical"
    res["point"] = [Int(point.x), Int(point.y)]
    if let now = nowState(el) { res["now"] = now }
    return res
}

func opSetValue(_ a: [String: Any]) throws -> [String: Any] {
    let (el, pid) = try resolveTarget(a)
    try guardSameWindow(a, pid)
    let text = str(a["text"]) ?? ""
    if AXUIElementSetAttributeValue(el, kAXValueAttribute as CFString, text as CFTypeRef) == .success {
        var res: [String: Any] = ["method": "ax_value", "value": text]
        if let now = nowState(el) { res["now"] = now }
        return res
    }
    throw AxonError("readonly", "That element would not accept a value.",
                    "Focus it and use type instead.")
}

func opType(_ a: [String: Any]) throws -> [String: Any] {
    var res: [String: Any] = [:]
    if a["index"] != nil || a["selector"] != nil {
        let (el, pid) = try resolveTarget(a)
        try guardSameWindow(a, pid)
        let setErr = AXUIElementSetAttributeValue(el, kAXFocusedAttribute as CFString, true as CFTypeRef)
        // Keystrokes follow the keyboard focus, not the element we named. If
        // focusing it did not take, typing anyway would put the text into
        // whatever the user has in front - the one failure worse than nothing.
        let landed = (axAttr(el, kAXFocusedAttribute as String) as? Bool) ?? false
        if setErr != .success && !landed {
            throw AxonError("focus_failed",
                "Could not put the keyboard focus on that element, so the text would have gone to whatever window is in front instead.",
                "Use replace:true, which writes through the element's value attribute and needs no focus at all.")
        }
    } else {
        let waited = try guardDisturb(a)
        if waited > 0 { res["waited_for_user_ms"] = waited }
    }
    let text = str(a["text"]) ?? ""
    let src = CGEventSource(stateID: .hidSystemState)
    for ch in text.unicodeScalars {
        var utf16 = Array(String(ch).utf16)
        if let down = markedEvent(CGEvent(keyboardEventSource: src, virtualKey: 0, keyDown: true)) {
            down.keyboardSetUnicodeString(stringLength: utf16.count, unicodeString: &utf16)
            down.post(tap: .cghidEventTap)
        }
        if let up = markedEvent(CGEvent(keyboardEventSource: src, virtualKey: 0, keyDown: false)) {
            up.keyboardSetUnicodeString(stringLength: utf16.count, unicodeString: &utf16)
            up.post(tap: .cghidEventTap)
        }
    }
    res["typed"] = text.count
    return res
}

let keyCodes: [String: CGKeyCode] = [
    "return": 36, "enter": 36, "tab": 48, "space": 49, "delete": 51, "backspace": 51,
    "escape": 53, "esc": 53, "left": 123, "right": 124, "down": 125, "up": 126,
    "home": 115, "end": 119, "pageup": 116, "pagedown": 121,
    "f1": 122, "f2": 120, "f3": 99, "f4": 118, "f5": 96, "f6": 97,
    "f7": 98, "f8": 100, "f9": 101, "f10": 109, "f11": 103, "f12": 111,
    "a": 0, "s": 1, "d": 2, "f": 3, "h": 4, "g": 5, "z": 6, "x": 7, "c": 8, "v": 9,
    "b": 11, "q": 12, "w": 13, "e": 14, "r": 15, "y": 16, "t": 17, "o": 31, "u": 32,
    "i": 34, "p": 35, "l": 37, "j": 38, "k": 40, "n": 45, "m": 46,
]

func opKey(_ a: [String: Any]) throws -> [String: Any] {
    var res: [String: Any] = [:]
    let chord = (str(a["keys"]) ?? "").lowercased()
    if chord.isEmpty { throw AxonError("bad_keys", "Empty key chord.", nil) }
    let waited = try guardDisturb(a)
    if waited > 0 { res["waited_for_user_ms"] = waited }

    var parts = chord.split(separator: "+").map { String($0) }
    guard let last = parts.popLast(), let code = keyCodes[last] else {
        throw AxonError("unknown_key", "Unrecognised key in '\(chord)'.",
                        "Use names like cmd+s, alt+f4, enter, tab, f5.")
    }
    var flags: CGEventFlags = []
    for m in parts {
        switch m {
        case "cmd", "command", "meta", "win": flags.insert(.maskCommand)
        case "ctrl", "control": flags.insert(.maskControl)
        case "alt", "option": flags.insert(.maskAlternate)
        case "shift": flags.insert(.maskShift)
        default: throw AxonError("unknown_key", "Unrecognised modifier '\(m)'.", nil)
        }
    }
    let src = CGEventSource(stateID: .hidSystemState)
    if let down = markedEvent(CGEvent(keyboardEventSource: src, virtualKey: code, keyDown: true)) {
        down.flags = flags
        down.post(tap: .cghidEventTap)
    }
    if let up = markedEvent(CGEvent(keyboardEventSource: src, virtualKey: code, keyDown: false)) {
        up.flags = flags
        up.post(tap: .cghidEventTap)
    }
    res["sent"] = chord
    return res
}

func opScroll(_ a: [String: Any]) throws -> [String: Any] {
    var res: [String: Any] = [:]
    let amount = int(a["amount"], -3)
    let horizontal = bool(a["horizontal"], false)

    // Park the pointer over the target first, since a scroll event goes to
    // whatever is under the cursor.
    if a["index"] != nil || a["selector"] != nil {
        let (el, pid) = try resolveTarget(a)
        try guardSameWindow(a, pid)
        if let r = axRect(el) {
            let waited = try guardDisturb(a)
            if waited > 0 { res["waited_for_user_ms"] = waited }
            let p = CGPoint(x: Double(r[0] + r[2] / 2), y: Double(r[1] + r[3] / 2))
            let src = CGEventSource(stateID: .hidSystemState)
            markedEvent(CGEvent(mouseEventSource: src, mouseType: .mouseMoved,
                                mouseCursorPosition: p, mouseButton: .left))?.post(tap: .cghidEventTap)
        }
    } else {
        let waited = try guardDisturb(a)
        if waited > 0 { res["waited_for_user_ms"] = waited }
    }

    let src = CGEventSource(stateID: .hidSystemState)
    let ev = CGEvent(scrollWheelEvent2Source: src, units: .line,
                     wheelCount: 1,
                     wheel1: horizontal ? 0 : Int32(amount),
                     wheel2: horizontal ? Int32(amount) : 0,
                     wheel3: 0)
    markedEvent(ev)?.post(tap: .cghidEventTap)
    res["method"] = "wheel"
    res["delta"] = amount
    return res
}

func opFocus(_ a: [String: Any]) throws -> [String: Any] {
    var res: [String: Any] = [:]
    let (win, pid, title) = try resolveWindow(a)
    let waited = try guardDisturb(a)
    if waited > 0 { res["waited_for_user_ms"] = waited }
    AXUIElementSetAttributeValue(win, kAXMinimizedAttribute as CFString, false as CFTypeRef)
    let ok = NSRunningApplication(processIdentifier: pid)?.activate(options: [.activateIgnoringOtherApps]) ?? false
    AXUIElementPerformAction(win, kAXRaiseAction as CFString)
    res["focused"] = ok
    res["hwnd"] = int(a["hwnd"], Int(pid) * 1000)
    res["title"] = title
    return res
}

func opCloseWindow(_ a: [String: Any]) throws -> [String: Any] {
    let (win, _, title) = try resolveWindow(a)
    // Press the window's own close button, so the app can still offer to save.
    // Axon has no way to terminate a process, on either platform.
    if let closeBtn = axAttr(win, kAXCloseButtonAttribute as String),
       CFGetTypeID(closeBtn) == AXUIElementGetTypeID() {
        AXUIElementPerformAction(closeBtn as! AXUIElement, "AXPress" as CFString)
    }
    Thread.sleep(forTimeInterval: 0.25)
    return ["closed": title, "still_open": axAttr(win, kAXRoleAttribute as String) != nil]
}

func opWaitFor(_ a: [String: Any]) throws -> [String: Any] {
    let timeout = int(a["timeout_ms"], 5000)
    let selector = a["selector"] as? [String: Any] ?? [:]
    let wantName = str(selector["name"])
    let wantRole = str(selector["role"])
    let deadline = Date().addingTimeInterval(Double(timeout) / 1000.0)
    let started = Date()

    var poll = a
    poll["register"] = false
    while Date() < deadline {
        if let snap = try? opSnapshot(poll), let nodes = snap["nodes"] as? [[String: Any]] {
            for n in nodes {
                let name = n["name"] as? String
                let role = n["role"] as? String
                if let w = wantName, name != w { continue }
                if let w = wantRole, role != w && role != "AX" + w { continue }
                if wantName == nil && wantRole == nil { continue }
                return [
                    "found": true,
                    "role": role as Any,
                    "name": name as Any,
                    "rect": n["rect"] as Any,
                    "waited_ms": Int(Date().timeIntervalSince(started) * 1000),
                ]
            }
        }
        Thread.sleep(forTimeInterval: 0.2)
    }
    throw AxonError("wait_timeout", "Element did not appear within \(timeout)ms.",
                    "Take a snapshot to see the current state.")
}

func opScreenshot(_ a: [String: Any]) throws -> [String: Any] {
    let maxWidth = int(a["max_width"], 1200)
    let quality = max(20, min(95, int(a["quality"], 60)))

    var rect = CGRect.infinite
    if a["hwnd"] != nil || a["title"] != nil {
        let (win, _, _) = try resolveWindow(a)
        guard let r = axRect(win) else {
            throw AxonError("window_rect_failed", "Could not read that window's bounds.", nil)
        }
        rect = CGRect(x: Double(r[0]), y: Double(r[1]), width: Double(r[2]), height: Double(r[3]))
    }

    guard let image = CGWindowListCreateImage(rect, .optionOnScreenOnly, kCGNullWindowID, [.bestResolution]) else {
        throw AxonError("capture_failed", "Screen capture returned nothing.",
                        "Grant Screen Recording permission in System Settings > Privacy & Security.")
    }

    let scale = min(1.0, Double(maxWidth) / Double(image.width))
    let nw = max(1, Int(Double(image.width) * scale))
    let nh = max(1, Int(Double(image.height) * scale))

    let rep = NSBitmapImageRep(cgImage: image)
    let resized = NSImage(size: NSSize(width: nw, height: nh))
    resized.lockFocus()
    NSImage(size: rep.size, flipped: false) { r in rep.draw(in: r); return true }
        .draw(in: NSRect(x: 0, y: 0, width: nw, height: nh))
    resized.unlockFocus()

    guard let tiff = resized.tiffRepresentation,
          let bitmap = NSBitmapImageRep(data: tiff),
          let jpeg = bitmap.representation(using: .jpeg, properties: [.compressionFactor: Double(quality) / 100.0]) else {
        throw AxonError("encode_failed", "Could not encode the capture as JPEG.", nil)
    }

    return [
        "mime": "image/jpeg",
        "width": nw,
        "height": nh,
        "bytes": jpeg.count,
        "data": jpeg.base64EncodedString(),
    ]
}

func opPresence(_ a: [String: Any]) throws -> [String: Any] {
    var r = Presence.shared.report()
    r["user_active"] = Presence.shared.active(idleThresholdMs)
    r["user_busy"] = Presence.shared.busy(idleThresholdMs)
    r["idle_threshold_ms"] = idleThresholdMs
    r["mode"] = defaultMode
    // Protocol parity with the Windows host. macOS has no on-screen Stop banner
    // and no BlockInput, so these are constant here, but the MCP layer reads
    // them uniformly and must not see them missing.
    r["stop_requested"] = false
    r["input_blocked"] = false
    let front = NSWorkspace.shared.frontmostApplication
    r["foreground_hwnd"] = Int(front?.processIdentifier ?? 0)
    r["foreground_process"] = front?.localizedName as Any
    r["foreground_title"] = front?.localizedName as Any
    return r
}

// Protocol parity with the Windows host, which uses this to place and label its
// on-screen banner when more than one Claude session is running. macOS has no
// banner, so there is nothing to place - but the op has to exist, or the MCP
// layer would retry a failing call on every action.
func opSession(_ a: [String: Any]) throws -> [String: Any] {
    return ["ok": true, "banner": false]
}

func opPing(_ a: [String: Any]) throws -> [String: Any] {
    return [
        "ok": true,
        "dpi_mode": "native",
        "platform": "darwin",
        "accessibility": trusted(),
        "snapshots": snapshots.count,
    ]
}

// MARK: - Dispatch

func dispatch(_ op: String, _ a: [String: Any]) throws -> [String: Any] {
    switch op {
    case "ping": return try opPing(a)
    case "presence": return try opPresence(a)
    case "session": return try opSession(a)
    case "list_apps": return try opListApps(a)
    case "snapshot": return try opSnapshot(a)
    case "click": return try opClick(a)
    case "type": return try opType(a)
    case "set_value": return try opSetValue(a)
    case "key": return try opKey(a)
    case "scroll": return try opScroll(a)
    case "focus": return try opFocus(a)
    case "close_window": return try opCloseWindow(a)
    case "wait_for": return try opWaitFor(a)
    case "screenshot": return try opScreenshot(a)
    default: throw AxonError("unknown_op", "Unknown operation '\(op)'.", nil)
    }
}

func readLoop() {
    while let line = readLine(strippingNewline: true) {
        let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty { continue }
        var reqId: Any = NSNull()
        do {
            guard let data = trimmed.data(using: .utf8),
                  let req = try JSONSerialization.jsonObject(with: data) as? [String: Any] else { continue }
            reqId = req["id"] ?? NSNull()
            let op = str(req["op"]) ?? ""
            let a = req["args"] as? [String: Any] ?? [:]

            if op == "shutdown" {
                emit(["id": reqId, "ok": true, "result": ["bye": true]])
                exit(0)
            }
            let started = Date()
            let result = try dispatch(op, a)
            emit(["id": reqId, "ok": true,
                  "ms": Int(Date().timeIntervalSince(started) * 1000),
                  "result": result])
        } catch let e as AxonError {
            var err: [String: Any] = ["code": e.code, "message": e.message]
            if let h = e.hint { err["hint"] = h }
            emit(["id": reqId, "ok": false, "error": err])
        } catch {
            emit(["id": reqId, "ok": false,
                  "error": ["code": "host_error", "message": String(describing: error)]])
        }
    }
    // stdin closed: the server is gone, and so is any reason to keep pumping
    // the event tap. Without this a warm-up run would never exit.
    exit(0)
}

// MARK: - Entry

if let v = ProcessInfo.processInfo.environment["CU_IDLE_MS"], let n = Int(v) { idleThresholdMs = n }
if let v = ProcessInfo.processInfo.environment["CU_WAIT_MS"], let n = Int(v) { waitBudgetMs = n }
if let v = ProcessInfo.processInfo.environment["CU_MODE"], !v.isEmpty { defaultMode = v }

if CommandLine.arguments.contains("--warmup") {
    // build.mjs runs this once after compiling, to be sure the binary starts.
    emit(["event": "warm"])
    exit(0)
}

if CommandLine.arguments.contains("--self-test") {
    // One command a Mac user can run to see whether this host actually works
    // on their machine, without involving Claude at all. The exit code says
    // what the last line says, so a script can trust it.
    var ok = true
    print("axon macOS host self-test")
    let ax = trusted()
    if !ax { ok = false }
    print("  accessibility permission : \(ax ? "granted" : "DENIED - grant it in System Settings > Privacy & Security > Accessibility")")
    Presence.shared.start()
    RunLoop.current.run(until: Date().addingTimeInterval(0.5))
    print("  presence monitoring      : \(Presence.shared.monitoring ? "active" : "UNAVAILABLE")")
    do {
        let apps = try opListApps([:])
        let count = (apps["windows"] as? [[String: Any]])?.count ?? 0
        print("  windows visible          : \(count)")
        if count == 0 { ok = false }
        print(count > 0 ? "PASS" : "FAIL: no windows enumerated")
    } catch let e as AxonError {
        print("  list_apps                : FAILED \(e.code) - \(e.message)")
        print("FAIL")
        ok = false
    } catch {
        print("FAIL: \(error)")
        ok = false
    }
    exit(ok ? 0 : 1)
}

Presence.shared.start()
emit(["event": "ready", "dpi_mode": "native", "platform": "darwin",
      "pid": ProcessInfo.processInfo.processIdentifier,
      "presence": Presence.shared.monitoring,
      "accessibility": trusted()])

// The event tap needs a live run loop, so the protocol is served from a
// background thread while the main thread pumps.
Thread.detachNewThread { readLoop() }
RunLoop.current.run()
