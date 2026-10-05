// Read-only offscreen inventory/capture safety regression using a self-owned
// window. Ordering it out models unavailable window pixels, not a real Space
// switch. Never changes Spaces, activates another app, or posts input events.
// Run: swift HiddenWindowCapture.swift <PaddiBridge> <report.json>
import AppKit
import CoreGraphics

precondition(CommandLine.arguments.count == 3)
let bridge=CommandLine.arguments[1], reportPath=CommandLine.arguments[2]
let app=NSApplication.shared; app.setActivationPolicy(.accessory)
let frontBefore=NSWorkspace.shared.frontmostApplication?.processIdentifier
let pointerBefore=CGEvent(source:nil)!.location
final class ColourView:NSView {
    var colour=NSColor(calibratedRed:0.1,green:0.8,blue:0.1,alpha:1)
    override func draw(_ dirtyRect:NSRect) { colour.setFill(); bounds.fill() }
}
let screen=NSScreen.main!.visibleFrame
let window=NSWindow(contentRect:NSRect(x:screen.maxX-300,y:screen.maxY-260,width:270,height:210),
    styleMask:[.titled],backing:.buffered,defer:false)
window.title="Paddi hidden capture regression"
let view=ColourView(frame:NSRect(x:0,y:0,width:270,height:210))
window.contentView=view; window.orderFront(nil)
let process=Process(), input=Pipe(), output=Pipe(), error=Pipe()
var done=false, passed=false
func finish(_ success:Bool, _ report:[String:Any]) {
    if done { return }; done=true; passed=success
    var value=report
    value["passed"]=success
    value["systemPointerUnchanged"]=pointerBefore == CGEvent(source:nil)!.location
    value["frontmostUnchanged"]=frontBefore == NSWorkspace.shared.frontmostApplication?.processIdentifier
    value["actualSpaceSwitchTested"]=false
    if let json=try? JSONSerialization.data(withJSONObject:value,options:[.prettyPrinted,.sortedKeys]) {
        try? json.write(to:URL(fileURLWithPath:reportPath)); print(String(data:json,encoding:.utf8)!)
    }
    if process.isRunning { process.terminate() }
    window.orderOut(nil); app.stop(nil)
    if let wake=NSEvent.otherEvent(with:.applicationDefined,location:.zero,modifierFlags:[],timestamp:0,
        windowNumber:0,context:nil,subtype:0,data1:0,data2:0) { app.postEvent(wake,atStart:false) }
}
func readLine(_ handle:FileHandle) -> Data? {
    var data=Data()
    while let byte=try? handle.read(upToCount:1), !byte.isEmpty {
        if byte[0]==10 { return data }; data.append(byte)
    }
    return data.isEmpty ? nil : data
}
func capture() throws -> [String:Any] {
    try input.fileHandleForWriting.write(contentsOf:Data("\(window.windowNumber)\n".utf8))
    guard let line=readLine(output.fileHandleForReading),
          let reply=try JSONSerialization.jsonObject(with:line) as? [String:Any] else {
        throw NSError(domain:"No capture reply",code:1)
    }
    return reply
}
func colour(_ reply:[String:Any]) -> NSColor? {
    guard let encoded=reply["png"] as? String, let png=Data(base64Encoded:encoded),
          let image=NSBitmapImageRep(data:png) else { return nil }
    return image.colorAt(x:image.pixelsWide/2,y:image.pixelsHigh/2)?.usingColorSpace(.deviceRGB)
}
func describeOwnWindow() throws -> [String:Any]? {
    let query=Process(), pipe=Pipe()
    query.executableURL=URL(fileURLWithPath:bridge); query.arguments=["list"]
    query.standardOutput=pipe; query.standardError=FileHandle.nullDevice
    try query.run(); let data=pipe.fileHandleForReading.readDataToEndOfFile(); query.waitUntilExit()
    guard query.terminationStatus == 0,
          let rows=try JSONSerialization.jsonObject(with:data) as? [[String:Any]] else {
        throw NSError(domain:"Window inventory failed",code:1)
    }
    return rows.first { ($0["id"] as? Int) == window.windowNumber && ($0["pid"] as? Int) == Int(getpid()) }
}
Timer.scheduledTimer(withTimeInterval:0.2,repeats:false) { _ in
    process.executableURL=URL(fileURLWithPath:bridge); process.arguments=["capture-server"]
    process.standardInput=input; process.standardOutput=output; process.standardError=error
    DispatchQueue.global().async {
        do {
            guard let before=try describeOwnWindow(), before["onScreen"] as? Bool == true else {
                throw NSError(domain:"Initial own window not listed",code:1)
            }
            DispatchQueue.main.sync { window.orderOut(nil) }
            Thread.sleep(forTimeInterval:0.2)
            guard let hidden=try describeOwnWindow(), hidden["onScreen"] as? Bool == false else {
                throw NSError(domain:"Offscreen window disappeared from all-window inventory",code:1)
            }
            DispatchQueue.main.sync { window.orderFront(nil) }
            try process.run()
            let first=try capture()
            guard let initial=colour(first), initial.greenComponent>0.5 else {
                throw NSError(domain:"Initial own window not captured",code:1)
            }
            DispatchQueue.main.sync { window.orderOut(nil) }
            Thread.sleep(forTimeInterval:0.2)
            let unavailable=try capture()
            guard let problem=unavailable["error"] as? String,
                  unavailable["png"] == nil, !problem.contains("目标窗口已关闭") else {
                throw NSError(domain:"Unavailable window reused old pixels or was mistaken for a close",code:1)
            }
            DispatchQueue.main.sync {
                view.colour=NSColor(calibratedRed:0.1,green:0.1,blue:0.85,alpha:1)
                window.orderFront(nil); view.needsDisplay=true; view.displayIfNeeded()
            }
            let started=ProcessInfo.processInfo.systemUptime
            var recovered:[String:Any]?
            while ProcessInfo.processInfo.systemUptime-started<4 {
                let next=try capture()
                if let value=colour(next), value.blueComponent>0.5 { recovered=next; break }
                Thread.sleep(forTimeInterval:0.05)
            }
            guard let recovered=recovered else { throw NSError(domain:"Own window did not recover fresh blue pixels",code:1) }
            let report:[String:Any]=[
                "hiddenWindowListed":true,"hiddenOnScreen":false,
                "unavailablePixelsRejected":true,"unavailableMessage":problem,
                "recoveredFreshBluePixels":true,
                "recoveryMs":(ProcessInfo.processInfo.systemUptime-started)*1000,
                "recoveredStreamStatus":recovered["streamStatus"] ?? NSNull(),
                "recoveredCallbackAgeMs":recovered["callbackAgeMs"] ?? NSNull()
            ]
            DispatchQueue.main.async {
                let safe=pointerBefore == CGEvent(source:nil)!.location &&
                    frontBefore == NSWorkspace.shared.frontmostApplication?.processIdentifier
                finish(safe,report)
            }
        } catch { DispatchQueue.main.async { finish(false,["error":"\(error)"]) } }
    }
}
Timer.scheduledTimer(withTimeInterval:12,repeats:false) { _ in finish(false,["error":"Capture fixture timed out"]) }
app.run(); exit(passed ? 0 : 1)
