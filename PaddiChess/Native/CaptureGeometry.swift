import CoreGraphics
import Foundation

// ScreenCaptureKit configurations use pixels; window/filter bounds use points.
// Keep full backing resolution rather than erasing small Chinese glyph strokes.
struct WindowCaptureGeometry: Equatable {
    static let maximumPixels = 16_777_216 // 64 MiB BGRA per surface; the stream keeps three.
    let width: Int
    let height: Int
    let pointPixelScale: Double

    init(contentRect: CGRect, pointPixelScale: Double) throws {
        guard contentRect.width.isFinite, contentRect.height.isFinite,
              contentRect.width > 0, contentRect.height > 0,
              pointPixelScale.isFinite, (1...4).contains(pointPixelScale) else {
            throw NSError(domain:"窗口截图尺寸或显示缩放比例无效", code:1)
        }
        let pixelWidth = ceil(contentRect.width * pointPixelScale)
        let pixelHeight = ceil(contentRect.height * pointPixelScale)
        // Bound before converting to Int or allocating. Never quietly lower the
        // resolution; the user can reduce an exceptionally large target window.
        guard pixelWidth <= Double(Self.maximumPixels), pixelHeight <= Double(Self.maximumPixels),
              pixelWidth * pixelHeight <= Double(Self.maximumPixels) else {
            throw NSError(domain:"窗口原始截图超过 1600 万像素，请缩小目标窗口后重试；为保留棋子细节，未自动降低清晰度", code:1)
        }
        width = Int(pixelWidth); height = Int(pixelHeight)
        self.pointPixelScale = pointPixelScale
    }

    func acceptsFrameScale(_ scale: Double) -> Bool {
        scale.isFinite && abs(scale - pointPixelScale) < 0.01
    }
}
