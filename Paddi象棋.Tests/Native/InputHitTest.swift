// Standalone native regression: creates only two tiny, nonactivating test panels;
// never sends clicks, moves the pointer, or changes another application's windows.
// Run: swift PikaDesk.Tests/Native/InputHitTest.swift <path-to-PaddiBridge>
import AppKit
import CoreGraphics

precondition(CommandLine.arguments.count == 2)
let bridge=CommandLine.arguments[1]
let app=NSApplication.shared
app.setActivationPolicy(.prohibited)
let primaryHeight=CGDisplayBounds(CGMainDisplayID()).height
let frame=NSScreen.screens.first!.visibleFrame
let rect=NSRect(x:frame.maxX-70,y:frame.maxY-70,width:40,height:40)
func panel(_ level:Int, _ color:NSColor) -> NSPanel {
    let result=NSPanel(contentRect:rect,styleMask:[.borderless,.nonactivatingPanel],backing:.buffered,defer:false)
    result.level=NSWindow.Level(rawValue:level)
    result.isOpaque=false;result.backgroundColor=color;result.hasShadow=false
    result.hidesOnDeactivate=false
    return result
}
let target=panel(NSWindow.Level.popUpMenu.rawValue+1,NSColor.gray.withAlphaComponent(0.2))
let overlay=panel(NSWindow.Level.popUpMenu.rawValue+2,NSColor.blue.withAlphaComponent(0.1))
defer {overlay.orderOut(nil);target.orderOut(nil)}
target.orderFrontRegardless()
func hit() throws -> Int {
    RunLoop.current.run(until:Date(timeIntervalSinceNow:0.1))
    let process=Process();process.executableURL=URL(fileURLWithPath:bridge)
    process.arguments=["hit-test",String(Double(rect.midX)),String(Double(primaryHeight-rect.midY))]
    let pipe=Pipe();process.standardOutput=pipe
    try process.run();process.waitUntilExit()
    precondition(process.terminationStatus == 0)
    let value=try JSONSerialization.jsonObject(with:pipe.fileHandleForReading.readDataToEndOfFile()) as! [String:Int]
    return value["windowId"]!
}
let uncovered=try hit();precondition(uncovered == target.windowNumber,"Uncovered target must receive the mouse")
overlay.ignoresMouseEvents=true;overlay.orderFrontRegardless()
let clickThrough=try hit();precondition(clickThrough == target.windowNumber,"Click-through overlay must not block the target")
overlay.ignoresMouseEvents=false
let interactive=try hit();precondition(interactive == overlay.windowNumber,"Interactive overlay must block the target")
overlay.orderOut(nil)
let restored=try hit();precondition(restored == target.windowNumber,"Removing overlay must restore target")
print("PASS: target, click-through overlay, interactive overlay, restored target; no input events sent")
