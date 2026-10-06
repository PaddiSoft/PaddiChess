// Read-only ScreenCaptureKit regression. Creates its own AppKit window and
// captures only that window through PaddiBridge; never sends mouse or keyboard
// events and never captures a user's game.
// Run: swift PaddiChess.Tests/Native/CaptureHeartbeat.swift <path-to-PaddiBridge> raw
import AppKit
import CoreGraphics

precondition(CommandLine.arguments.count >= 2)
let raw = CommandLine.arguments.dropFirst(2).first == "raw"
var lastPixels: Data?
let bridge = CommandLine.arguments[1]
let app = NSApplication.shared
app.setActivationPolicy(.accessory)
let oldFront = NSWorkspace.shared.frontmostApplication
let pointerBefore = CGEvent(source:nil)!.location
final class ColourView: NSView {
    var colour = NSColor(calibratedRed:0.10,green:0.75,blue:0.15,alpha:1)
    override func draw(_ dirtyRect: NSRect) { colour.setFill(); bounds.fill() }
}
let screen = NSScreen.main!.visibleFrame
let window = NSWindow(contentRect:NSRect(x:screen.maxX-300,y:screen.maxY-260,width:270,height:210),
                      styleMask:[.titled],backing:.buffered,defer:false)
window.title = "Paddi isolated capture regression"
let view = ColourView(frame:NSRect(x:0,y:0,width:270,height:210))
window.contentView = view
window.orderFront(nil)
let process = Process()
let input = Pipe(), output = Pipe(), error = Pipe()
var completed = false
var passed = false
var errorText = ""
func finish(_ success:Bool, _ message:String) {
    if completed { return }
    completed=true; passed=success
    print(message)
    if process.isRunning { process.terminate() }
    window.orderOut(nil)
    if NSWorkspace.shared.frontmostApplication?.processIdentifier == getpid() { oldFront?.activate(options:[]) }
    app.stop(nil)
    if let wake=NSEvent.otherEvent(with:.applicationDefined,location:.zero,modifierFlags:[],timestamp:0,
                                  windowNumber:0,context:nil,subtype:0,data1:0,data2:0) { app.postEvent(wake,atStart:false) }
}
func readLine(_ handle:FileHandle) -> Data? {
    var data=Data()
    while let byte=try? handle.read(upToCount:1), !byte.isEmpty {
        if byte[0]==10 { return data }
        data.append(byte)
    }
    return data.isEmpty ? nil : data
}
func capture() throws -> (Int,Double,NSColor,[String:Any]) {
    try input.fileHandleForWriting.write(contentsOf:Data("\(window.windowNumber)\n".utf8))
    guard let line=readLine(output.fileHandleForReading),
          let reply=try JSONSerialization.jsonObject(with:line) as? [String:Any] else {
        throw NSError(domain:"capture server terminated",code:1)
    }
    if let problem=reply["error"] as? String { throw NSError(domain:problem,code:1) }
    let colour: NSColor
    if raw {
        let width = reply["width"] as! Int, height = reply["height"] as! Int, stride = reply["rowBytes"] as! Int
        let count = reply["byteCount"] as! Int
        if count > 0 {
            var data = Data()
            while data.count < count {
                guard let next = try output.fileHandleForReading.read(upToCount:count-data.count), !next.isEmpty else {
                    throw NSError(domain:"truncated raw frame",code:1)
                }
                data.append(next)
            }
            lastPixels = data
        }
        guard let pixels = lastPixels, pixels.count == stride*height else { throw NSError(domain:"missing raw frame",code:1) }
        let expected = DispatchQueue.main.sync {
            (Int(ceil(window.frame.width * window.backingScaleFactor)),
             Int(ceil(window.frame.height * window.backingScaleFactor)))
        }
        guard width == expected.0, height == expected.1 else {
            throw NSError(domain:"window backing pixels were resized: \(width)x\(height), expected \(expected.0)x\(expected.1)",code:1)
        }
        let offset = (height/2)*stride+(width/2)*4
        colour = NSColor(deviceRed:Double(pixels[offset+2])/255,green:Double(pixels[offset+1])/255,blue:Double(pixels[offset])/255,alpha:1)
    } else {
        guard let encoded=reply["png"] as? String, let png=Data(base64Encoded:encoded),
              let image=NSBitmapImageRep(data:png),
              let decoded=image.colorAt(x:image.pixelsWide/2,y:image.pixelsHigh/2)?.usingColorSpace(.deviceRGB) else {
            throw NSError(domain:"capture PNG not readable",code:1)
        }
        colour = decoded
    }
    return ((reply["sequence"] as? Int) ?? -1,(reply["frameAgeMs"] as? Double) ?? -1,colour,reply)
}
Timer.scheduledTimer(withTimeInterval:0.2,repeats:false) { _ in
    process.executableURL=URL(fileURLWithPath:bridge)
    process.arguments=[raw ? "capture-server-raw" : "capture-server"]
    process.standardInput=input; process.standardOutput=output; process.standardError=error
    do { try process.run() } catch { finish(false,"FAIL: \(error)"); return }
    DispatchQueue.global().async {
        errorText=String(data:error.fileHandleForReading.readDataToEndOfFile(),encoding:.utf8) ?? ""
    }
    DispatchQueue.global().async {
        do {
            let first=try capture()
            guard first.2.greenComponent > 0.5, first.2.greenComponent > first.2.redComponent + 0.2 else {
                throw NSError(domain:"initial green window not captured (RGB \(first.2.redComponent), \(first.2.greenComponent), \(first.2.blueComponent))",code:1)
            }
            // Old pixel content is valid when the producer continues reporting idle.
            // This deliberate quiet interval must not be interpreted as a frozen board.
            Thread.sleep(forTimeInterval:2)
            let idle=try capture()
            guard idle.2.greenComponent > 0.5 else { throw NSError(domain:"static capture changed",code:1) }
            DispatchQueue.main.async {
                view.colour=NSColor(calibratedRed:0.05,green:0.1,blue:0.85,alpha:1)
                view.needsDisplay=true
                view.displayIfNeeded()
            }
            let began=ProcessInfo.processInfo.systemUptime
            var updated:(Int,Double,NSColor,[String:Any])?
            while ProcessInfo.processInfo.systemUptime-began < 3 {
                Thread.sleep(forTimeInterval:0.05)
                let reply=try capture()
                if reply.2.blueComponent>0.5, reply.2.blueComponent>reply.2.greenComponent+0.2 { updated=reply; break }
            }
            guard let updated=updated else { throw NSError(domain:"changed own window stayed on cached green pixels",code:1) }
            let elapsed=(ProcessInfo.processInfo.systemUptime-began)*1000
            // A logical resize must invalidate the stream's encoded packet even
            // if the restarted producer reuses its old sequence number.
            DispatchQueue.main.sync {
                window.setContentSize(NSSize(width:320,height:240))
                view.needsDisplay=true; view.displayIfNeeded()
            }
            Thread.sleep(forTimeInterval:0.1)
            let resized = try capture()
            guard resized.2.blueComponent > 0.5 else { throw NSError(domain:"resized window pixels stale",code:1) }
            var timings:[Double]=[]
            for index in 0..<20 {
                DispatchQueue.main.sync {
                    view.colour=NSColor(calibratedRed:Double(index%2)*0.8,green:0.15,blue:Double((index+1)%2)*0.8,alpha:1)
                    view.needsDisplay=true; view.displayIfNeeded()
                }
                Thread.sleep(forTimeInterval:0.03)
                let start=ProcessInfo.processInfo.systemUptime
                _ = try capture()
                timings.append((ProcessInfo.processInfo.systemUptime-start)*1000)
            }
            let unchanged=pointerBefore==CGEvent(source:nil)!.location
            let metrics:[String:Any] = ["transport":raw ? "BGRA" : "PNG", "initialSequence":first.0,"idleSequence":idle.0,
                "initialPixelWidth":first.3["width"] ?? NSNull(),"initialPixelHeight":first.3["height"] ?? NSNull(),
                "resizedPixelWidth":resized.3["width"] ?? NSNull(),"resizedPixelHeight":resized.3["height"] ?? NSNull(),
                "idlePixelAgeMs":idle.1,"updatedSequence":updated.0,"updatedPixelAgeMs":updated.1,
                "requestMedianMs":timings.sorted()[timings.count/2], "requestMaxMs":timings.max()!, "updateLatencyMs":elapsed,"systemPointerUnchanged":unchanged,
                "idleCallbackAgeMs":idle.3["callbackAgeMs"] ?? NSNull(),
                "idleStreamStatus":idle.3["streamStatus"] ?? NSNull()]
            let result=String(data:try JSONSerialization.data(withJSONObject:metrics,options:[.sortedKeys]),encoding:.utf8)!
            DispatchQueue.main.async { finish(unchanged,"\(unchanged ? "PASS" : "FAIL"): \(result)") }
        } catch { DispatchQueue.main.async { finish(false,"FAIL: \(error) \(errorText)") } }
    }
}
Timer.scheduledTimer(withTimeInterval:12,repeats:false) { _ in finish(false,"FAIL: capture regression timed out \(errorText)") }
app.run()
exit(passed ? 0 : 1)
