import AppKit
import Foundation

let output = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "PikaDesk.png"
let size = NSSize(width: 1024, height: 1024)
let image = NSImage(size: size)
image.lockFocus()

let background = NSBezierPath(roundedRect: NSRect(x: 20, y: 20, width: 984, height: 984), xRadius: 210, yRadius: 210)
NSColor(calibratedRed: 0.61, green: 0.18, blue: 0.21, alpha: 1).setFill()
background.fill()

let edge = NSBezierPath(ovalIn: NSRect(x: 152, y: 152, width: 720, height: 720))
NSColor(calibratedRed: 0.99, green: 0.90, blue: 0.75, alpha: 1).setFill()
edge.fill()
NSColor(calibratedRed: 0.77, green: 0.53, blue: 0.34, alpha: 1).setStroke()
edge.lineWidth = 22
edge.stroke()

let inner = NSBezierPath(ovalIn: NSRect(x: 196, y: 196, width: 632, height: 632))
inner.lineWidth = 5
inner.stroke()

let font = NSFont(name: "PingFangSC-Semibold", size: 525) ?? NSFont.systemFont(ofSize: 525, weight: .bold)
let text = "象" as NSString
let attributes: [NSAttributedString.Key: Any] = [
    .font: font,
    .foregroundColor: NSColor(calibratedRed: 0.56, green: 0.17, blue: 0.19, alpha: 1)
]
let textSize = text.size(withAttributes: attributes)
text.draw(at: NSPoint(x: (1024 - textSize.width) / 2, y: (1024 - textSize.height) / 2 + 22), withAttributes: attributes)

image.unlockFocus()
guard let tiff = image.tiffRepresentation,
      let bitmap = NSBitmapImageRep(data: tiff),
      let png = bitmap.representation(using: .png, properties: [:]) else {
    fatalError("Could not render app icon")
}
try png.write(to: URL(fileURLWithPath: output))
