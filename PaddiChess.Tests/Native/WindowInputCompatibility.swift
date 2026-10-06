// Controlled checks for actual AppKit click-through behavior. Only self-owned
// windows receive events. Focused scenarios explicitly activate this fixture;
// the original application is restored at the end and the pointer never moves.
// Run: swift WindowInputCompatibility.swift <PaddiBridge> <scenario> <report.json>
// Scenarios: background-clickthrough, background-default, focused-default,
// focused-sibling, focused-delayed-hover, focused-source-focus-switch.
import AppKit
import CoreGraphics

precondition(CommandLine.arguments.count == 4)
let bridge = CommandLine.arguments[1], scenario = CommandLine.arguments[2]
let reportPath = CommandLine.arguments[3]
let allowed = ["background-clickthrough", "background-default", "focused-default",
               "focused-sibling", "focused-delayed-hover", "focused-source-focus-switch", "background-cancel",
               "background-cancel-held-click", "background-cancel-drag"]
precondition(allowed.contains(scenario))
let focused = scenario.hasPrefix("focused-")
let originalFront = NSWorkspace.shared.frontmostApplication
let app = NSApplication.shared
app.setActivationPolicy(focused ? .regular : .accessory)
let pointerBefore = CGEvent(source:nil)!.location
let startedAt = ProcessInfo.processInfo.systemUptime

final class Receiver: NSView {
    let clickThrough: Bool, hoverDelay: Double
    var hover = NSPoint(x:-1,y:-1)
    var downs: [NSPoint] = [], ups: [NSPoint] = []
    var staleHover = false, firstMouseQueries = 0
    var afterFirstUp: (() -> Void)?
    var afterFirstDown: (() -> Void)?
    var afterFirstDrag: (() -> Void)?
    var dragCount = 0
    init(frame:NSRect, clickThrough:Bool, hoverDelay:Double) {
        self.clickThrough = clickThrough; self.hoverDelay = hoverDelay
        super.init(frame:frame)
    }
    required init?(coder:NSCoder) { fatalError() }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event:NSEvent?) -> Bool {
        firstMouseQueries += 1
        return clickThrough
    }
    override func updateTrackingAreas() {
        for area in trackingAreas { removeTrackingArea(area) }
        // Real AppKit tracking, not an EventWindow forwarding mouseDown itself.
        addTrackingArea(NSTrackingArea(rect:bounds,
            options:[.mouseMoved,.activeAlways,.inVisibleRect],owner:self,userInfo:nil))
        super.updateTrackingAreas()
    }
    override func mouseMoved(with event:NSEvent) {
        let next = event.locationInWindow
        DispatchQueue.main.asyncAfter(deadline:.now()+hoverDelay) { self.hover=next }
    }
    override func mouseDown(with event:NSEvent) {
        let point = event.locationInWindow
        staleHover = staleHover || abs(hover.x-point.x)>2 || abs(hover.y-point.y)>2
        downs.append(point)
        if downs.count == 1 { afterFirstDown?() }
    }
    override func mouseDragged(with event:NSEvent) {
        dragCount += 1
        if dragCount == 1 { afterFirstDrag?() }
    }
    override func mouseUp(with event:NSEvent) {
        ups.append(event.locationInWindow)
        if ups.count == 1 { afterFirstUp?() }
    }
}
final class EventWindow: NSWindow {
    var moveEvents = 0, downEvents = 0
    override func sendEvent(_ event:NSEvent) {
        if event.type == .mouseMoved { moveEvents += 1 }
        if event.type == .leftMouseDown { downEvents += 1 }
        super.sendEvent(event)
    }
}

let screen = NSScreen.main!.visibleFrame
let window = EventWindow(contentRect:NSRect(x:screen.maxX-320,y:screen.maxY-280,width:280,height:220),
    styleMask:[.titled],backing:.buffered,defer:false)
window.title = "Paddi window compatibility receiver"
window.acceptsMouseMovedEvents = true
let receiver = Receiver(frame:NSRect(x:0,y:0,width:280,height:220),
    clickThrough:scenario == "background-clickthrough",
    hoverDelay:scenario == "focused-delayed-hover" ? 0.05 : 0.025)
window.contentView = receiver; window.makeFirstResponder(receiver)
window.orderFront(nil)
let sibling = EventWindow(contentRect:NSRect(x:screen.maxX-650,y:screen.maxY-280,width:280,height:220),
    styleMask:[.titled],backing:.buffered,defer:false)
sibling.title = "Paddi window compatibility sibling"
if scenario == "focused-source-focus-switch" {
    receiver.afterFirstUp = { sibling.makeKeyAndOrderFront(nil) }
}

let process = Process(), output = Pipe(), error = Pipe()
if scenario == "background-cancel" { receiver.afterFirstUp = { process.terminate() } }
if scenario == "background-cancel-held-click" { receiver.afterFirstDown = { process.terminate() } }
if scenario == "background-cancel-drag" { receiver.afterFirstDrag = { process.terminate() } }
var done = false, passed = false, backgroundFocusChanged = false
var expectedY = 0.0
var commandStartedAt = 0.0
func finish(_ success:Bool, _ message:String) {
    if done { return }; done=true; passed=success
    let pointerAfter = CGEvent(source:nil)!.location
    let report: [String:Any] = [
        "scenario":scenario, "passed":success, "message":message,
        "durationMs":(ProcessInfo.processInfo.systemUptime-startedAt)*1000,
        "commandMs":commandStartedAt > 0 ? (ProcessInfo.processInfo.systemUptime-commandStartedAt)*1000 : 0,
        "receiverDown":receiver.downs.map { [Double($0.x),Double($0.y)] },
        "receiverUp":receiver.ups.map { [Double($0.x),Double($0.y)] },
        "nativeWindowDownEvents":window.downEvents, "hoverEvents":window.moveEvents,
        "staleHover":receiver.staleHover, "firstMouseQueries":receiver.firstMouseQueries,
        "siblingDownEvents":sibling.downEvents,
        "dragEvents":receiver.dragCount,
        "systemPointerUnchanged":pointerBefore == pointerAfter,
        "frontmostTarget":NSWorkspace.shared.frontmostApplication?.processIdentifier == getpid(),
        "backgroundFrontmostUnchanged":!backgroundFocusChanged,
        "targetKeyWindow":window.isKeyWindow,
        "expectedY":expectedY,
        "receiverActiveAfter":app.isActive
    ]
    if let json = try? JSONSerialization.data(withJSONObject:report,options:[.prettyPrinted,.sortedKeys]) {
        try? json.write(to:URL(fileURLWithPath:reportPath))
        print(String(data:json,encoding:.utf8)!)
    }
    window.orderOut(nil); sibling.orderOut(nil)
    if NSWorkspace.shared.frontmostApplication?.processIdentifier == getpid() {
        originalFront?.activate(options:[])
    }
    app.stop(nil)
    if let wake=NSEvent.otherEvent(with:.applicationDefined,location:.zero,modifierFlags:[],timestamp:0,
        windowNumber:0,context:nil,subtype:0,data1:0,data2:0) { app.postEvent(wake,atStart:false) }
}

Timer.scheduledTimer(withTimeInterval:0.2,repeats:false) { _ in
    if !focused {
        app.deactivate(); window.resignKey()
        if originalFront?.processIdentifier != getpid() { originalFront?.activate(options:[]) }
    }
    if scenario == "focused-sibling" {
        app.activate(ignoringOtherApps:true)
        sibling.makeKeyAndOrderFront(nil)
    }
    DispatchQueue.main.asyncAfter(deadline:.now()+0.15) {
        if !focused && (app.isActive || window.isKeyWindow) {
            finish(false,"Receiver did not start inactive"); return
        }
        if scenario == "focused-sibling" && !sibling.isKeyWindow {
            finish(false,"Sibling did not acquire fixture focus"); return
        }
        let rows=CGWindowListCopyWindowInfo([.optionAll,.excludeDesktopElements],kCGNullWindowID) as! [[String:Any]]
        guard let row=rows.first(where: { $0[kCGWindowNumber as String] as? Int == window.windowNumber }),
              let bounds=row[kCGWindowBounds as String] as? [String:Double] else {
            finish(false,"Receiver missing from window inventory"); return
        }
        let x=bounds["X"]!, y=bounds["Y"]!, width=bounds["Width"]!, height=bounds["Height"]!
        expectedY = height-110
        process.executableURL=URL(fileURLWithPath:bridge)
        process.arguments=["move",String(window.windowNumber),String(x),String(y),String(width),String(height),
            String(x+50),String(y+110),String(x+220),String(y+110),scenario == "background-cancel-drag" ? "drag" : "click",focused ? "window-focused" : "window"]
        process.standardOutput=output; process.standardError=error
        process.terminationHandler={ task in
            DispatchQueue.main.asyncAfter(deadline:.now()+0.12) {
                let message=String(data:error.fileHandleForReading.readDataToEndOfFile(),encoding:.utf8) ?? ""
                let pointerUnchanged=pointerBefore == CGEvent(source:nil)!.location
                let cancelled=scenario.hasPrefix("background-cancel")
                let focusSwitched=scenario == "focused-source-focus-switch"
                let clicksCorrect=focusSwitched || cancelled
                    ? (receiver.downs.count == 1 && receiver.ups.count == 1 && !receiver.staleHover &&
                        abs(receiver.ups[0].x-50)<2 &&
                        (cancelled ? message.contains("已取消") : message.hasPrefix("input-blocked:") && message.contains("\"inputStarted\":true")))
                    : (receiver.downs.count == 2 && receiver.ups.count == 2 && !receiver.staleHover &&
                        abs(receiver.downs[0].x-50)<2 && abs(receiver.downs[1].x-220)<2 &&
                        receiver.downs.allSatisfy { abs(Double($0.y)-expectedY)<2 })
                let focusCorrect=focusSwitched ? sibling.isKeyWindow : focused
                    ? (NSWorkspace.shared.frontmostApplication?.processIdentifier == getpid() && window.isKeyWindow)
                    : !backgroundFocusChanged && !app.isActive
                let statusCorrect=focusSwitched || cancelled ? task.terminationStatus != 0 : task.terminationStatus == 0
                finish(statusCorrect && clicksCorrect && pointerUnchanged && focusCorrect && sibling.downEvents == 0,
                    focusSwitched ? "A focus switch after selection is reported as partial input; destination and sibling are untouched. \(message)"
                    : cancelled ? "Cancellation restored the inactive application and omitted the destination. \(message)"
                        : "Source and destination reached the intended window. \(message)")
            }
        }
        do { commandStartedAt=ProcessInfo.processInfo.systemUptime; try process.run() }
        catch { finish(false,"\(error)") }
    }
}
Timer.scheduledTimer(withTimeInterval:0.01,repeats:true) { _ in
    if commandStartedAt > 0 && !focused && NSWorkspace.shared.frontmostApplication?.processIdentifier != originalFront?.processIdentifier {
        backgroundFocusChanged=true
    }
}
Timer.scheduledTimer(withTimeInterval:10,repeats:false) { _ in
    if process.isRunning { process.terminate() }
    finish(false,"Input receiver timed out")
}
app.run()
exit(passed ? 0 : 1)
