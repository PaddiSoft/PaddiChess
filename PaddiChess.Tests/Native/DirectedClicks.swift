// Controlled native regression for PID-directed input. It creates its own window,
// checks background delivery, deferred hover and click dispatch, and never clicks
// another application's UI or activates the receiver's application.
// Run: swift PikaDesk.Tests/Native/DirectedClicks.swift <path-to-PaddiBridge>
import AppKit
import CoreGraphics

precondition(CommandLine.arguments.count == 2)
let bridge=CommandLine.arguments[1]
let app=NSApplication.shared
app.setActivationPolicy(.accessory)
let oldFront=NSWorkspace.shared.frontmostApplication
let pointerBefore=CGEvent(source:nil)!.location
final class Receiver:NSView {
    var hover:CGFloat = -1
    var clicks:[CGFloat] = []
    var eventLocations:[CGFloat] = []
    var badHover=false
    override var acceptsFirstResponder:Bool { true }
    override func updateTrackingAreas() {
        for area in trackingAreas { removeTrackingArea(area) }
        addTrackingArea(NSTrackingArea(rect:bounds,options:[.mouseMoved,.activeAlways,.inVisibleRect],owner:self,userInfo:nil))
        super.updateTrackingAreas()
    }
    override func acceptsFirstMouse(for event:NSEvent?) -> Bool { true }
    override func mouseMoved(with event:NSEvent) {
        let x=event.locationInWindow.x
        // Reproduce games whose render loop updates the hover target on the next frame.
        DispatchQueue.main.asyncAfter(deadline:.now()+0.025) { self.hover=x }
    }
    override func mouseDown(with event:NSEvent) {
        badHover = badHover || abs(hover-event.locationInWindow.x)>2
        eventLocations.append(event.locationInWindow.x)
    }
    override func mouseUp(with event:NSEvent) { clicks.append(hover) }
}
final class EventWindow:NSWindow {
    var moves=0
    override func sendEvent(_ event:NSEvent) {
        if event.type == .mouseMoved { moves += 1 }
        super.sendEvent(event)
    }
}
let screen=NSScreen.main!.visibleFrame
let window=EventWindow(contentRect:NSRect(x:screen.maxX-310,y:screen.maxY-270,width:280,height:220),
                    styleMask:[.titled],backing:.buffered,defer:false)
window.title="Paddi directed-input regression"
window.acceptsMouseMovedEvents=true
let receiver=Receiver(frame:NSRect(x:0,y:0,width:280,height:220))
window.contentView=receiver
// A foreground-only receiver would miss this regression: keep this window
// inactive so the helper must deliver without raising or activating it.
window.orderFront(nil)
window.makeFirstResponder(receiver)
var done=false
var passed=false
var focusChanged=false
let process=Process()
let output=Pipe();let error=Pipe()
func finish(_ success:Bool, _ message:String) {
    if done { return };done=true;passed=success
    print(message)
    window.orderOut(nil)
    if NSWorkspace.shared.frontmostApplication?.processIdentifier == getpid() { oldFront?.activate(options:[]) }
    app.stop(nil)
    if let wake=NSEvent.otherEvent(with:.applicationDefined,location:.zero,modifierFlags:[],timestamp:0,
                                  windowNumber:0,context:nil,subtype:0,data1:0,data2:0) { app.postEvent(wake,atStart:false) }
}
Timer.scheduledTimer(withTimeInterval:0.2,repeats:false) { _ in
    guard !app.isActive, !window.isKeyWindow else { finish(false,"FAIL: receiver must start in the background"); return }
    let rows=CGWindowListCopyWindowInfo([.optionOnScreenOnly,.excludeDesktopElements],kCGNullWindowID) as! [[String:Any]]
    let row=rows.first { $0[kCGWindowNumber as String] as? Int == window.windowNumber }!
    let bounds=row[kCGWindowBounds as String] as! [String:Double]
    let x=bounds["X"]!,y=bounds["Y"]!,width=bounds["Width"]!,height=bounds["Height"]!
    process.executableURL=URL(fileURLWithPath:bridge)
    // Target points are in the client area, below its title bar.
    process.arguments=["move",String(window.windowNumber),String(x),String(y),String(width),String(height),
                       String(x+50),String(y+110),String(x+220),String(y+110),"click","window"]
    process.standardOutput=output;process.standardError=error
    process.terminationHandler={ task in
        DispatchQueue.main.asyncAfter(deadline:.now()+0.1) {
            let text=String(data:error.fileHandleForReading.readDataToEndOfFile(),encoding:.utf8) ?? ""
            let pointerAfter=CGEvent(source:nil)!.location
            let frontmostUnchanged = !focusChanged && NSWorkspace.shared.frontmostApplication?.processIdentifier == oldFront?.processIdentifier
            let ok=task.terminationStatus == 0 && receiver.clicks.count == 2 && !receiver.badHover &&
                   abs(receiver.clicks[0]-50)<2 && abs(receiver.clicks[1]-220)<2 && pointerBefore == pointerAfter && frontmostUnchanged
            finish(ok,"\(ok ? "PASS" : "FAIL"): clicks=\(receiver.clicks), down=\(receiver.eventLocations), moveEvents=\(window.moves), staleHover=\(receiver.badHover), systemPointerUnchanged=\(pointerBefore == pointerAfter), frontmostUnchanged=\(frontmostUnchanged) \(text)")
        }
    }
    do { try process.run() } catch { finish(false,"FAIL: \(error)") }
}
Timer.scheduledTimer(withTimeInterval:0.01,repeats:true) { _ in
    if NSWorkspace.shared.frontmostApplication?.processIdentifier != oldFront?.processIdentifier { focusChanged=true }
}
Timer.scheduledTimer(withTimeInterval:8,repeats:false) { _ in
    if process.isRunning { process.terminate() }
    finish(false,"FAIL: input receiver timed out")
}
app.run()
exit(passed ? 0 : 1)
