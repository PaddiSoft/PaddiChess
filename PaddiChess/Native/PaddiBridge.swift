import AppKit
import CoreGraphics
import ApplicationServices
import ScreenCaptureKit
import Vision
import CoreMedia
import VideoToolbox
import Darwin

struct Target: Codable {
    let id: Int; let pid: Int; let title: String
    let x: Double; let y: Double; let width: Double; let height: Double
    let onScreen: Bool
}
func targets(windowId:CGWindowID? = nil) -> [Target] {
    // A window on another Space is still the same live target. Restricting this
    // inventory to the current desktop falsely treated Space switches as closes.
    // Capture must independently supply a valid current frame before any input.
    let options:CGWindowListOption = windowId == nil
        ? [.optionAll, .excludeDesktopElements] : [.optionIncludingWindow, .excludeDesktopElements]
    let rows = CGWindowListCopyWindowInfo(options, windowId ?? kCGNullWindowID) as? [[String: Any]] ?? []
    return rows.compactMap { row in
        guard let id = row[kCGWindowNumber as String] as? Int,
              let pid = row[kCGWindowOwnerPID as String] as? Int,
              let bounds = row[kCGWindowBounds as String] as? [String: Double],
              let w = bounds["Width"], let h = bounds["Height"], w > 180, h > 180,
              (row[kCGWindowLayer as String] as? Int ?? 1) == 0 else { return nil }
        let owner = row[kCGWindowOwnerName as String] as? String ?? ""
        let title = row[kCGWindowName as String] as? String ?? ""
        return Target(id:id, pid:pid, title:owner + " · " + title,
                      x:bounds["X"] ?? 0, y:bounds["Y"] ?? 0, width:w, height:h,
                      onScreen:row[kCGWindowIsOnscreen as String] as? Bool ?? false)
    }
}
// While a button is down, interruption releases at the source. A dragged
// piece must not be accidentally submitted at an intermediate square.
var heldPoint: CGPoint?
var inputTarget: Target?
var focusedWindowInput = false
var focusedInputWindow: AXUIElement?
var inputCancelled: Int32 = 0
var backgroundRestore: (() -> Void)?
func restoreBackgroundInput() {
    let restore = backgroundRestore; backgroundRestore = nil; restore?()
}
func waitForInput(_ seconds:Double) {
    // Foreground compatibility mode monitors workspace focus between the two
    // clicks; keep its notification cache current while pacing the input.
    let deadline = Date(timeIntervalSinceNow:seconds)
    repeat {
        if inputCancelled != 0 { fail("落子操作已取消") }
        RunLoop.current.run(until:min(deadline,Date(timeIntervalSinceNow:0.01)))
    } while Date() < deadline
}
// CGEventPostToPid routes input but does not establish the recipient's AppKit
// activation/key-window context. Supply it locally, without SetFrontProcess,
// AXRaise, changing Spaces or touching the user's real pointer. The legacy
// WindowServer record layout is also used by yabai's make_key_window routine:
// https://github.com/koekeishiya/yabai/blob/master/src/window_manager.c
// Resolve private/legacy symbols dynamically; no injection or SIP changes.
func prepareBackgroundInput(_ target:Target) {
    guard NSWorkspace.shared.frontmostApplication?.processIdentifier != pid_t(target.pid) else { return }
    typealias PidLookup = @convention(c) (Int32, UnsafeMutablePointer<ProcessSerialNumber>) -> Int32
    typealias RecordPost = @convention(c) (UnsafePointer<ProcessSerialNumber>, UnsafeRawPointer) -> Int32
    let scope = UnsafeMutableRawPointer(bitPattern:-2)
    guard let lookupSymbol=dlsym(scope,"GetProcessForPID"), let postSymbol=dlsym(scope,"SLPSPostEventRecordTo") else {
        inputBlocked("当前系统缺少后台窗口输入接口；请选择“窗口事件 · 自动聚焦”。")
    }
    let lookup=unsafeBitCast(lookupSymbol,to:PidLookup.self), post=unsafeBitCast(postSymbol,to:RecordPost.self)
    var psn=ProcessSerialNumber()
    guard lookup(pid_t(target.pid),&psn) == 0 else { inputBlocked("目标应用不可接收后台事件，等待窗口恢复。") }
    func appState(_ subtype:Int16) {
        NSEvent.otherEvent(with:.appKitDefined,location:.zero,modifierFlags:[],timestamp:ProcessInfo.processInfo.systemUptime,
            windowNumber:target.id,context:nil,subtype:subtype,data1:0,data2:0)?.cgEvent?.postToPid(pid_t(target.pid))
    }
    backgroundRestore = {
        // A real user activation during the submission takes precedence over
        // our temporary background context. Never deactivate that real focus.
        if NSWorkspace.shared.frontmostApplication?.processIdentifier != pid_t(target.pid) { appState(2) }
    }
    appState(1)
    var bytes=[UInt8](repeating:0,count:0xf8)
    bytes[4]=0xf8; bytes[0x3a]=0x10
    var windowId=UInt32(target.id)
    withUnsafeBytes(of:&windowId) { bytes.replaceSubrange(0x3c..<0x40,with:$0) }
    // The sentinel position produces key-window activation, not a click in
    // the board content. Source/destination input remains separately guarded.
    for i in 0x20..<0x30 { bytes[i]=0xff }
    for type:UInt8 in [1,2] {
        bytes[8]=type
        let status=bytes.withUnsafeBytes { post(&psn,$0.baseAddress!) }
        if status != 0 { inputBlocked("后台窗口上下文建立失败，尚未发送棋盘点击。") }
    }
    waitForInput(0.04)
}
// Window-relative mouse coordinates are exported by Quartz but absent from its
// public headers. Resolve at runtime and keep this compatibility mode opt-in.
typealias SetWindowLocation = @convention(c) (CGEvent, CGPoint) -> Void
let windowLocationSetter: SetWindowLocation? = dlsym(UnsafeMutableRawPointer(bitPattern:-2), "CGEventSetWindowLocation")
    .map { unsafeBitCast($0,to:SetWindowLocation.self) }
let windowMouseSource = CGEventSource(stateID:.privateState)
var directedPointer: CGPoint?
var clickNumber: Int64 = 0
func postMouse(_ type:CGEventType, _ point:CGPoint) {
    let event: CGEvent?
    if let target = inputTarget {
        event = CGEvent(mouseEventSource:windowMouseSource,mouseType:type,mouseCursorPosition:point,mouseButton:.left)
        event?.setIntegerValueField(CGEventField(rawValue:51)!,value:Int64(target.id))
        if let event = event { windowLocationSetter?(event,CGPoint(x:point.x-target.x,y:point.y-target.y)) }
    } else { event = CGEvent(mouseEventSource:nil,mouseType:type,mouseCursorPosition:point,mouseButton:.left) }
    guard let event = event else { fail("无法创建鼠标事件") }
    let isMove = type == .mouseMoved || type == .leftMouseDragged
    event.setIntegerValueField(.mouseEventClickState,value:isMove ? 0 : 1)
    if type == .leftMouseDown { clickNumber += 1 }
    event.setIntegerValueField(.mouseEventNumber,value:clickNumber)
    event.setDoubleValueField(.mouseEventPressure,value:type == .leftMouseDown || type == .leftMouseDragged ? 1 : 0)
    if let target = inputTarget {
        if isMove, let previous = directedPointer {
            event.setIntegerValueField(.mouseEventDeltaX,value:Int64((point.x-previous.x).rounded()))
            event.setIntegerValueField(.mouseEventDeltaY,value:Int64((point.y-previous.y).rounded()))
        }
        directedPointer = point
        event.setIntegerValueField(.mouseEventWindowUnderMousePointer,value:Int64(target.id))
        event.setIntegerValueField(.mouseEventWindowUnderMousePointerThatCanHandleThisEvent,value:Int64(target.id))
        event.postToPid(pid_t(target.pid))
    } else { event.post(tap:.cghidEventTap) }
}
func prepareDirectedPointer(_ point:CGPoint, target:Target) {
    // Give Web/Metal views real, paced hover updates. Posting move/down/up in
    // one burst lets the render loop consume the previous hover target instead.
    if let start = directedPointer {
        for step in 1...4 {
            let next = CGPoint(x:start.x+(point.x-start.x)*Double(step)/4,
                               y:start.y+(point.y-start.y)*Double(step)/4)
            verifyVisible(next,target:target)
            postMouse(.mouseMoved,next)
            waitForInput(0.012)
        }
    } else { postMouse(.mouseMoved,point) }
    // The target application's event queue and a frame-based hover update both
    // need time to run. A 40 ms settle intermittently beat a delayed AppKit view;
    // 75 ms also leaves two frames for 30 Hz mirrored/game surfaces.
    waitForInput(0.075)
}
var inputStarted = false
func fail(_ message:String) -> Never {
    if let point = heldPoint { heldPoint = nil; postMouse(.leftMouseUp,point) }
    restoreBackgroundInput()
 FileHandle.standardError.write(Data(message.utf8)); exit(1) }
func inputBlocked(_ message:String) -> Never {
    let value:[String:Any] = ["inputStarted":inputStarted, "message":message]
    let data = try! JSONSerialization.data(withJSONObject:value)
    fail("input-blocked:" + String(data:data,encoding:.utf8)!)
}
func mouseTargetWindow(at point:CGPoint) -> Int {
    // NSWindow's documented mouse-down hit test honors click-through windows. A CGWindow
    // rectangle/z-order test also sees non-interactive recording decorations and is insufficient.
    _ = NSApplication.shared
    // Quartz uses a top-left origin; AppKit uses bottom-left. Both global systems are anchored
    // to the primary display, including when the point is on a secondary or negative-coordinate display.
    let primaryHeight = CGDisplayBounds(CGMainDisplayID()).height
    return NSWindow.windowNumber(at:NSPoint(x:point.x,y:primaryHeight-point.y),belowWindowWithWindowNumber:0)
}
func verifyVisible(_ point:CGPoint, target:Target) {
    if inputCancelled != 0 { fail("落子操作已取消") }
    guard let current=targets(windowId:CGWindowID(target.id)).first(where:{$0.id==target.id}), current.pid==target.pid,
          abs(current.x-target.x)<1, abs(current.y-target.y)<1,
          abs(current.width-target.width)<1, abs(current.height-target.height)<1 else { fail("目标窗口移动，接管已暂停") }
    guard !CGEventSource.keyState(.combinedSessionState,key:53) else { fail("已按 Escape 停止") }
    guard point.x>target.x, point.x<target.x+target.width, point.y>target.y, point.y<target.y+target.height else { fail("落点超出目标窗口") }
    if inputTarget != nil {
        if focusedWindowInput && !current.onScreen {
            inputBlocked("目标棋盘不在当前桌面，前台兼容模式暂不落子；请返回目标桌面。后台窗口事件仅适用于接受后台输入的目标。")
        }
        // Do not turn a focus change in the middle of an explicit foreground
        // submission into an unacknowledged destination-only/background click.
        if focusedWindowInput && NSWorkspace.shared.frontmostApplication?.processIdentifier != pid_t(target.pid) {
            inputBlocked("目标窗口焦点已切换，正在等待已提交落子的画面确认；请将目标窗口置于前台。")
        }
        if focusedWindowInput, let window = focusedInputWindow {
            var mainValue: CFTypeRef?
            if AXUIElementCopyAttributeValue(window,kAXMainAttribute as CFString,&mainValue) == .success,
               let main = mainValue as? Bool, !main {
                inputBlocked("同一应用的其他窗口已获得焦点，正在等待已提交落子的画面确认；请返回目标棋盘窗口。")
            }
        }
        return // PID routing never clicks an overlapping application.
    }
    guard current.onScreen else { inputBlocked("目标窗口不在当前桌面，系统鼠标暂不落子；请返回目标桌面，或选择兼容的窗口事件模式。") }
    let hit = mouseTargetWindow(at:point)
    guard hit == target.id else {
        let rows=CGWindowListCopyWindowInfo([.optionOnScreenOnly,.excludeDesktopElements],kCGNullWindowID) as? [[String:Any]] ?? []
        let owner=rows.first(where:{$0[kCGWindowNumber as String] as? Int == hit})?[kCGWindowOwnerName as String] as? String
        let message = owner.map { "落点当前由「\($0)」接收鼠标，正在等待目标棋盘可操作。" }
            ?? "尚未确认目标棋盘可接收鼠标，正在等待窗口可操作。"
        inputBlocked(message)
    }
}
// On-device Chinese glyph recognition for first-time / middle-game attachment.
// Input contains just the selected window image; output never leaves this process.
func recognizeBoard() throws {
    guard let line = readLine(), let data = line.data(using:.utf8),
          let obj = try JSONSerialization.jsonObject(with:data) as? [String:Any],
          let encoded = obj["png"] as? String, let bytes = Data(base64Encoded:encoded),
          let rep = NSBitmapImageRep(data:bytes), let source = rep.cgImage,
          let left = obj["left"] as? Double, let top = obj["top"] as? Double,
          let right = obj["right"] as? Double, let bottom = obj["bottom"] as? Double else { fail("识别输入无效") }
    let dx = (right-left)/8, dy = (bottom-top)/9
    var results: [[String:Any]] = []
    for row in 0..<10 { for col in 0..<9 {
        autoreleasepool {
            let cx=left+Double(col)*dx, cy=top+Double(row)*dy
            let rect=CGRect(x:cx-dx*0.27,y:cy-dy*0.27,width:dx*0.54,height:dy*0.54).integral
            guard let crop=source.cropping(to:rect),
                  let context=CGContext(data:nil,width:192,height:192,bitsPerComponent:8,bytesPerRow:192*4,space:CGColorSpaceCreateDeviceRGB(),bitmapInfo:CGImageAlphaInfo.premultipliedLast.rawValue) else {
                results.append(["text":"", "confidence":0]); return
            }
            context.interpolationQuality = .high; context.draw(crop,in:CGRect(x:0,y:0,width:192,height:192))
            guard let enlarged=context.makeImage() else { results.append(["text":"", "confidence":0]); return }
            // Single ornate glyphs are often rejected by text detectors. Normalize ink and
            // repeat the glyph on a white strip so the detector sees a short text line.
            let pixels=context.data!.bindMemory(to:UInt8.self,capacity:192*192*4)
            var mean=0.0
            for p in stride(from:0,to:192*192*4,by:4) { mean += Double(pixels[p])+Double(pixels[p+1])+Double(pixels[p+2]) }
            let darkDisc=mean/Double(192*192*3*255)<0.42
            for y in 0..<192 { for x in 0..<192 {
                let p=(y*192+x)*4, r=Int(pixels[p]),g=Int(pixels[p+1]),b=Int(pixels[p+2])
                let ink = darkDisc ? (r>170 && g>170 && b>150) : ((r<125 && g<125 && b<125) || (r-g>38 && r-b>38 && abs(g-b)<52))
                let value:UInt8 = ink ? 0 : 255
                pixels[p]=value; pixels[p+1]=value; pixels[p+2]=value; pixels[p+3]=255
            } }
            let strip=CGContext(data:nil,width:576,height:216,bitsPerComponent:8,bytesPerRow:576*4,space:CGColorSpaceCreateDeviceRGB(),bitmapInfo:CGImageAlphaInfo.premultipliedLast.rawValue)!
            strip.setFillColor(CGColor(gray:1,alpha:1)); strip.fill(CGRect(x:0,y:0,width:576,height:216))
            if let mask=context.makeImage() {
                for copy in 0..<3 { strip.draw(mask,in:CGRect(x:copy*192+24,y:36,width:144,height:144)) }
            }
            let request=VNRecognizeTextRequest()
            request.recognitionLevel = .accurate; request.usesLanguageCorrection = false
            request.recognitionLanguages = ["zh-Hans","zh-Hant","en-US"]
            request.minimumTextHeight = 0.15
            request.customWords = ["車","车","俥","馬","马","傌","象","相","士","仕","將","将","帥","帅","炮","砲","兵","卒"]
            do {
                try VNImageRequestHandler(cgImage:strip.makeImage() ?? enlarged,options:[:]).perform([request])
                let candidates = (request.results ?? []).flatMap { $0.topCandidates(3) }
                let allowed = Set("車车俥馬马傌象相士仕將将帥帅炮砲兵卒")
                let recognized=candidates.first { $0.string.contains { allowed.contains($0) } }
                results.append(["text":recognized?.string ?? "", "confidence":recognized?.confidence ?? 0])
            } catch { results.append(["text":"", "confidence":0]) }
        }
    } }
    let output = try JSONSerialization.data(withJSONObject:results)
    FileHandle.standardOutput.write(output); FileHandle.standardOutput.write(Data([10]))
}
// Read only clock text outside the calibrated board. A static position has no
// intrinsic side to move; a running clock can supply that missing evidence.
func recognizeClocks() throws {
    guard let line = readLine(), let data = line.data(using:.utf8),
          let obj = try JSONSerialization.jsonObject(with:data) as? [String:Any],
          let encoded = obj["png"] as? String, let bytes = Data(base64Encoded:encoded),
          let rep = NSBitmapImageRep(data:bytes), let source = rep.cgImage,
          let top = obj["top"] as? Double, let bottom = obj["bottom"] as? Double else { fail("计时器识别输入无效") }
    let width = Double(source.width), height = Double(source.height)
    let step = (bottom-top)/9
    func seconds(in rect:CGRect) -> Int? {
        guard rect.width > 80, rect.height > 24, let crop = source.cropping(to:rect.integral) else { return nil }
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .fast
        request.usesLanguageCorrection = false
        request.recognitionLanguages = ["en-US"]
        do { try VNImageRequestHandler(cgImage:crop,options:[:]).perform([request]) }
        catch { return nil }
        let pattern = try! NSRegularExpression(pattern:"(?<![0-9])([0-9]{1,2})[:：]([0-9]{2})(?![0-9])")
        let candidates = (request.results ?? []).compactMap { observation -> (Int,Float,Double)? in
            guard let result = observation.topCandidates(1).first else { return nil }
            let value = result.string as NSString
            guard let match = pattern.firstMatch(in:result.string,range:NSRange(location:0,length:value.length)),
                  let minutes = Int(value.substring(with:match.range(at:1))),
                  let rest = Int(value.substring(with:match.range(at:2))), rest < 60 else { return nil }
            return (minutes*60+rest,result.confidence,abs(Double(observation.boundingBox.midX)-0.5))
        }
        return candidates.sorted { a,b in
            let aScore = Double(a.1)-a.2*0.35, bScore = Double(b.1)-b.2*0.35
            return aScore > bScore
        }.first?.0
    }
    let upperHeight = max(0,top-step*0.35)
    let lowerY = min(height,bottom+step*0.35)
    let upper = seconds(in:CGRect(x:0,y:0,width:width,height:upperHeight))
    let lower = seconds(in:CGRect(x:0,y:lowerY,width:width,height:height-lowerY))
    let output: [String:Any] = ["top":upper as Any? ?? NSNull(),"bottom":lower as Any? ?? NSNull()]
    let dataOut = try JSONSerialization.data(withJSONObject:output)
    FileHandle.standardOutput.write(dataOut); FileHandle.standardOutput.write(Data([10]))
}
// ScreenCaptureKit produces frames continuously. Keep only the newest complete
// sample; a slow recognizer must never work through a backlog of old screenshots.
private enum CaptureGeometryChange: Error { case backingScale }
@available(macOS 14.0, *)
final class LatestWindowStream: NSObject, SCStreamOutput, SCStreamDelegate {
    let target: Target
    private let geometry: WindowCaptureGeometry
    private let condition = NSCondition()
    private let outputQueue = DispatchQueue(label:"com.paddisoft.xiangqi.capture", qos:.userInteractive)
    private var sample: CMSampleBuffer?
    private var sequence = 0
    private var receivedAt = 0.0
    private var callbackAt = 0.0
    private var latestStatus: SCFrameStatus?
    private var failure: Error?
    private var stream: SCStream?
    private var encodedSequence = -1
    private var encoded: Data?
    private var backingScaleChanged = false

    init(target:Target) throws {
        self.target = target
        let sem = DispatchSemaphore(value:0)
        var window: SCWindow?; var error: Error?
        SCShareableContent.getExcludingDesktopWindows(true,onScreenWindowsOnly:false) { content,err in
            window = content?.windows.first { $0.windowID == CGWindowID(target.id) }
            error = err; sem.signal()
        }
        guard sem.wait(timeout:.now()+8) == .success else { throw NSError(domain:"获取窗口超时",code:1) }
        if let error = error { throw error }
        guard let window = window else { throw NSError(domain:"目标窗口不可截图",code:1) }
        // Both APIs are available on macOS 14, matching this capture backend.
        // CGWindow bounds and SCContentFilter.contentRect are logical points;
        // SCStreamConfiguration.width/height are explicitly physical pixels.
        let filter = SCContentFilter(desktopIndependentWindow:window)
        geometry = try WindowCaptureGeometry(contentRect:filter.contentRect,
            pointPixelScale:Double(filter.pointPixelScale))
        super.init()
        let config = SCStreamConfiguration()
        config.width = geometry.width; config.height = geometry.height
        config.captureResolution = .best
        config.pixelFormat = kCVPixelFormatType_32BGRA
        config.minimumFrameInterval = CMTime(value:1,timescale:60)
        config.queueDepth = 3; config.showsCursor = false
        config.ignoreShadowsSingleWindow = true
        config.scalesToFit = false
        if #available(macOS 14.2, *) { config.includeChildWindows = true }
        let active = SCStream(filter:filter,configuration:config,delegate:self)
        stream = active
        try active.addStreamOutput(self,type:.screen,sampleHandlerQueue:outputQueue)
        active.startCapture { err in error = err; sem.signal() }
        guard sem.wait(timeout:.now()+8) == .success else { stop(); throw NSError(domain:"启动窗口捕获超时",code:1) }
        if let error = error { stop(); throw error }
    }
    func stream(_ stream:SCStream,didOutputSampleBuffer buffer:CMSampleBuffer,of type:SCStreamOutputType) {
        guard type == .screen,
              let rows = CMSampleBufferGetSampleAttachmentsArray(buffer,createIfNecessary:false) as? [[SCStreamFrameInfo:Any]],
              let raw = rows.first?[.status] as? Int, let status = SCFrameStatus(rawValue:raw) else { return }
        condition.lock()
        // Idle frames have no new pixels but are a fresh producer heartbeat. Pixel
        // age alone cannot distinguish a static board from a suspended stream.
        callbackAt = ProcessInfo.processInfo.systemUptime
        latestStatus = status
        // The same-sized window can move between Retina and non-Retina displays.
        // A fixed output surface must not stretch a newly changed backing scale
        // or keep serving an old cached board. Recreate from a fresh filter.
        if let scale = (rows.first?[.scaleFactor] as? NSNumber)?.doubleValue,
           !geometry.acceptsFrameScale(scale) {
            backingScaleChanged = true; sample = nil
            condition.broadcast(); condition.unlock(); return
        }
        switch status {
        case .complete, .started:
            if buffer.isValid, CMSampleBufferGetImageBuffer(buffer) != nil {
                sample = buffer; sequence += 1; receivedAt = callbackAt
            } else { sample = nil }
        case .idle:
            break // Reuse the last complete image only while the producer is alive.
        case .blank, .suspended, .stopped:
            // The old board is not a current observation. Do not let it authorize
            // another move while the screen/stream is unavailable.
            sample = nil
        @unknown default:
            sample = nil
        }
        condition.broadcast(); condition.unlock()
    }
    func stream(_ stream:SCStream,didStopWithError error:Error) {
        condition.lock(); failure = error; condition.broadcast(); condition.unlock()
    }
    func snapshot(raw:Bool = false) throws -> (Data,Int,Double,Double,String,Int,Int,Int) {
        condition.lock()
        let deadline = Date(timeIntervalSinceNow:3)
        while sample == nil && failure == nil && !backingScaleChanged {
            if latestStatus == .blank || latestStatus == .suspended || latestStatus == .stopped { break }
            if !condition.wait(until:deadline) { break }
        }
        let now = ProcessInfo.processInfo.systemUptime
        let buffer = sample, seq = sequence, age = (now-receivedAt)*1000,
            callbackAge = (now-callbackAt)*1000, status = latestStatus, error = failure,
            changed = backingScaleChanged
        condition.unlock()
        if changed { throw CaptureGeometryChange.backingScale }
        if let error = error { throw error }
        switch status {
        case .blank: throw NSError(domain:"窗口画面暂不可用，正在恢复捕获",code:1)
        case .suspended: throw NSError(domain:"窗口捕获已暂停，正在恢复捕获",code:1)
        case .stopped: throw NSError(domain:"窗口捕获流已停止，正在恢复捕获",code:1)
        default: break
        }
        guard callbackAge <= 5000 else { throw NSError(domain:"窗口捕获心跳超时，正在恢复捕获",code:1) }
        guard let buffer = buffer, let pixels = CMSampleBufferGetImageBuffer(buffer) else { throw NSError(domain:"等待窗口首帧超时",code:1) }
        let statusName = status == .idle ? "idle" : "complete"
        let width = CVPixelBufferGetWidth(pixels), height = CVPixelBufferGetHeight(pixels), stride = CVPixelBufferGetBytesPerRow(pixels)
        // Static windows reuse their PNG. Encoding happens on demand, not on all 60 frames.
        if seq == encodedSequence, let data = encoded { return (data,seq,max(0,age),max(0,callbackAge),statusName,width,height,stride) }
        if raw {
            CVPixelBufferLockBaseAddress(pixels,.readOnly)
            defer { CVPixelBufferUnlockBaseAddress(pixels,.readOnly) }
            guard let base = CVPixelBufferGetBaseAddress(pixels) else { throw NSError(domain:"无法读取截图像素",code:1) }
            let data = Data(bytes:base,count:stride*height)
            encoded = data; encodedSequence = seq
            return (data,seq,max(0,age),max(0,callbackAge),statusName,width,height,stride)
        }
        var image: CGImage?
        guard VTCreateCGImageFromCVPixelBuffer(pixels,options:nil,imageOut:&image) == noErr,
              let image = image,
              let data = NSBitmapImageRep(cgImage:image).representation(using:.png,properties:[:]) else {
            throw NSError(domain:"截图编码失败",code:1)
        }
        encoded = data; encodedSequence = seq
        return (data,seq,max(0,age),max(0,callbackAge),statusName,width,height,stride)
    }
    func matches(_ next:Target) -> Bool {
        condition.lock(); defer { condition.unlock() }
        return !backingScaleChanged && target.id == next.id && target.pid == next.pid &&
            abs(target.width-next.width)<1 && abs(target.height-next.height)<1
    }
    func stop() {
        guard let active = stream else { return }
        stream = nil
        let sem = DispatchSemaphore(value:0)
        active.stopCapture { _ in sem.signal() }
        _ = sem.wait(timeout:.now()+2)
    }
}
@available(macOS 14.0, *)
func captureServer(raw:Bool = false) {
    var source: LatestWindowStream?
    var sentSequence = -1
    defer { source?.stop() }
    while let line = readLine() {
        autoreleasepool {
            do {
                guard CGPreflightScreenCaptureAccess() else { throw NSError(domain:"permission:screen",code:1) }
                guard let id = Int(line), id > 0, id <= Int(UInt32.max), let target = targets(windowId:CGWindowID(id)).first(where: { $0.id == id }) else {
                    throw NSError(domain:"目标窗口已关闭或不再可捕获",code:1)
                }
                if source?.matches(target) != true {
                    source?.stop(); source = nil
                    source = try LatestWindowStream(target:target)
                    sentSequence = -1
                }
                let captured: (Data,Int,Double,Double,String,Int,Int,Int)
                do { captured = try source!.snapshot(raw:raw) }
                catch is CaptureGeometryChange {
                    // Scale may change after matches() while waiting for a frame.
                    // Keep this recovery inside the helper and invalidate both
                    // the encoded cache and the binary transport reuse marker.
                    source?.stop(); source = nil
                    source = try LatestWindowStream(target:target)
                    sentSequence = -1
                    captured = try source!.snapshot(raw:raw)
                }
                let (pixels,seq,age,callbackAge,status,width,height,stride) = captured
                let targetData = try JSONEncoder().encode(target)
                var obj: [String:Any] = ["window":try JSONSerialization.jsonObject(with:targetData),
                    "sequence":seq,"frameAgeMs":age,"callbackAgeMs":callbackAge,"streamStatus":status]
                if raw {
                    obj["width"] = width; obj["height"] = height; obj["rowBytes"] = stride
                    obj["byteCount"] = seq == sentSequence ? 0 : pixels.count
                } else { obj["png"] = pixels.base64EncodedString() }
                let data = try JSONSerialization.data(withJSONObject:obj)
                FileHandle.standardOutput.write(data); FileHandle.standardOutput.write(Data([10]))
                if raw && seq != sentSequence { FileHandle.standardOutput.write(pixels) }
                sentSequence = seq
            } catch {
                source?.stop(); source = nil
                let data = try! JSONSerialization.data(withJSONObject:["error":error.localizedDescription])
                FileHandle.standardOutput.write(data); FileHandle.standardOutput.write(Data([10]))
            }
        }
    }
}
let args = CommandLine.arguments
if args.count < 2 { fail("缺少操作") }
switch args[1] {
case "recognize":
    try recognizeBoard()
case "recognize-clocks":
    try recognizeClocks()
case "capture-server", "capture-server-raw":
    if #available(macOS 14.0, *) { captureServer(raw:args[1] == "capture-server-raw") } else { fail("持续截图需要 macOS 14 或更新系统") }
case "permissions":
    let result = ["screenCapture":CGPreflightScreenCaptureAccess(), "accessibility":AXIsProcessTrusted() && CGPreflightPostEventAccess()]
    print(String(data:try JSONSerialization.data(withJSONObject:result),encoding:.utf8)!)
case "request-screen":
    if !CGPreflightScreenCaptureAccess() {
        _ = CGRequestScreenCaptureAccess()
        NSWorkspace.shared.open(URL(string:"x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")!)
    }
    print("ok")
case "request-accessibility":
    if !AXIsProcessTrusted() || !CGPreflightPostEventAccess() {
        _ = AXIsProcessTrustedWithOptions([kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String:true] as CFDictionary)
        if !CGPreflightPostEventAccess() { _ = CGRequestPostEventAccess() }
        NSWorkspace.shared.open(URL(string:"x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
    }
    print("ok")
case "list":
    guard CGPreflightScreenCaptureAccess() else { fail("permission:screen") }
    print(String(data:try JSONEncoder().encode(targets()), encoding:.utf8)!)
case "capture":
    guard args.count == 4, let id = Int(args[2]), id > 0, id <= Int(UInt32.max), let target = targets(windowId:CGWindowID(id)).first(where:{$0.id == id}) else { fail("目标窗口已关闭或不再可捕获") }
    guard CGPreflightScreenCaptureAccess() else { fail("permission:screen") }
    let task = Process(); task.executableURL = URL(fileURLWithPath:"/usr/sbin/screencapture")
    task.arguments = ["-x", "-o", "-l", String(id), args[3]]
    try task.run(); task.waitUntilExit()
    guard task.terminationStatus == 0 else { fail("无法截取目标窗口，请检查录屏权限和窗口状态") }
    print(String(data:try JSONEncoder().encode(target),encoding:.utf8)!)
case "hit-test":
    // Read-only diagnostic: never activates a window or posts an input event.
    guard args.count == 4, let x=Double(args[2]), let y=Double(args[3]), x.isFinite, y.isFinite else { fail("坐标无效") }
    let hit = mouseTargetWindow(at:CGPoint(x:x,y:y))
    print("{\"windowId\":\(hit)}")
case "move":
    signal(SIGTERM) { _ in inputCancelled = 1 }
    guard (11...13).contains(args.count), let id=Int(args[2]), id > 0, id <= Int(UInt32.max), let target=targets(windowId:CGWindowID(id)).first(where:{$0.id == id}) else { fail("目标窗口不可用") }
    let nums=args[3...10].compactMap(Double.init)
    guard nums.count==8 else { fail("坐标无效") }
    guard abs(target.x-nums[0])<1, abs(target.y-nums[1])<1, abs(target.width-nums[2])<1, abs(target.height-nums[3])<1 else { fail("窗口位置或大小改变，请重新标定") }
    guard AXIsProcessTrusted() && CGPreflightPostEventAccess() else { fail("permission:accessibility") }
    if args.count >= 12 && args[11] == "finish-click" { inputStarted = true }
    let focusedWindow = args.count == 13 && args[12] == "window-focused"
    let directed = args.count == 13 && (args[12] == "window" || focusedWindow)
    if directed && windowLocationSetter == nil { inputBlocked("当前系统不支持窗口内鼠标坐标，请选择系统鼠标输入。") }
    inputTarget = directed ? target : nil
    focusedWindowInput = focusedWindow
    // Foreground window events are a separate explicit choice for views that
    // reject background click-through. They focus the target without moving the
    // physical pointer. Plain window events must never steal application focus.
    if !directed || focusedWindow {
        let wasFront = NSWorkspace.shared.frontmostApplication?.processIdentifier == pid_t(target.pid)
        NSRunningApplication(processIdentifier:pid_t(target.pid))?.activate(options:[])
        let appElement = AXUIElementCreateApplication(pid_t(target.pid))
        AXUIElementSetAttributeValue(appElement, kAXFrontmostAttribute as CFString, kCFBooleanTrue)
        var windowsValue: CFTypeRef?
        if AXUIElementCopyAttributeValue(appElement, kAXWindowsAttribute as CFString, &windowsValue) == .success,
           let windows = windowsValue as? [AXUIElement] {
            for window in windows {
                var positionValue: CFTypeRef?
                var sizeValue: CFTypeRef?
                if AXUIElementCopyAttributeValue(window, kAXPositionAttribute as CFString, &positionValue) != .success { continue }
                if AXUIElementCopyAttributeValue(window, kAXSizeAttribute as CFString, &sizeValue) != .success { continue }
                guard let rawPosition=positionValue, let rawSize=sizeValue,
                      CFGetTypeID(rawPosition)==AXValueGetTypeID(), CFGetTypeID(rawSize)==AXValueGetTypeID() else { continue }
                var position=CGPoint.zero; var size=CGSize.zero
                AXValueGetValue(unsafeBitCast(rawPosition,to:AXValue.self),.cgPoint,&position)
                AXValueGetValue(unsafeBitCast(rawSize,to:AXValue.self),.cgSize,&size)
                if abs(position.x-target.x)<2 && abs(position.y-target.y)<2 && abs(size.width-target.width)<2 && abs(size.height-target.height)<2 {
                    if focusedWindow { focusedInputWindow = window }
                    AXUIElementPerformAction(window, kAXRaiseAction as CFString)
                    AXUIElementSetAttributeValue(window,kAXMainAttribute as CFString,kCFBooleanTrue)
                    AXUIElementSetAttributeValue(window,kAXFocusedAttribute as CFString,kCFBooleanTrue)
                    AXUIElementSetAttributeValue(appElement,kAXFocusedWindowAttribute as CFString,window)
                    break
                }
            }
        }
        if !wasFront { waitForInput(0.3) }
        if focusedWindow {
            // Activation is asynchronous, especially when it switches Spaces.
            // Wait for that prerequisite before the source click; an inactive
            // view may otherwise discard it and consume only the destination.
            let deadline = ProcessInfo.processInfo.systemUptime + 1.0
            while NSWorkspace.shared.frontmostApplication?.processIdentifier != pid_t(target.pid) {
                guard !CGEventSource.keyState(.combinedSessionState,key:53) else { fail("已按 Escape 停止") }
                guard ProcessInfo.processInfo.systemUptime < deadline else {
                    inputBlocked("目标窗口尚未获得焦点，窗口事件暂不落子；请将目标窗口置于前台后继续。")
                }
                // NSWorkspace's activation state follows workspace notifications;
                // a command-line helper must pump its run loop to receive them.
                RunLoop.current.run(until:Date(timeIntervalSinceNow:0.02))
            }
        }
    }
    // Preflight both points before any mouse-down, then verify again immediately before input.
    verifyVisible(CGPoint(x:nums[4],y:nums[5]),target:target)
    verifyVisible(CGPoint(x:nums[6],y:nums[7]),target:target)
    if directed && !focusedWindow { prepareBackgroundInput(target) }
    let dragging = args.count >= 12 && args[11] == "drag"
    let finishSelection = args.count >= 12 && args[11] == "finish-click"
    var mouseHeld = false
    defer {
        if mouseHeld { postMouse(.leftMouseUp,CGPoint(x:nums[6],y:nums[7])) }
    }
    for i in (finishSelection ? [6] : [4,6]) {
        let point=CGPoint(x:nums[i], y:nums[i+1])
        verifyVisible(point,target:target)
        if directed && (!dragging || i == 4) { prepareDirectedPointer(point,target:target) }
        if dragging {
            if i == 4 {
                heldPoint = point; mouseHeld = true
                inputStarted = true
                postMouse(.leftMouseDown,point)
            } else {
                let start = CGPoint(x:nums[4], y:nums[5])
                for step in 1...10 {
                    let p = CGPoint(x:start.x+(point.x-start.x)*Double(step)/10, y:start.y+(point.y-start.y)*Double(step)/10)
                    verifyVisible(p,target:target)
                    postMouse(.leftMouseDragged,p)
                    waitForInput(0.015)
                }
                verifyVisible(point,target:target)
                postMouse(.leftMouseUp,point)
                mouseHeld = false; heldPoint = nil
            }
        } else {
            heldPoint = point
            inputStarted = true
            postMouse(.leftMouseDown,point)
            waitForInput(directed ? 0.075 : 0.060)
            postMouse(.leftMouseUp,point)
            heldPoint = nil
        }
        // Keep the source-selection interval for games and mirrored-device input;
        // the GUI keeps observing throughout this pacing interval.
        if i == 4 { waitForInput(0.18) }
    }
    // Keep the temporary context alive while a throttled background game
    // consumes the destination up event. Immediate deactivation can discard it.
    if directed && !focusedWindow { waitForInput(0.15) }
    restoreBackgroundInput()
    print("ok")
default: fail("未知操作")
}
