import CoreGraphics
import Foundation

@main
struct CaptureGeometryTests {
    static func main() throws {
        let phone = CGRect(x:0, y:0, width:387, height:670)
        let retina = try WindowCaptureGeometry(contentRect:phone, pointPixelScale:2)
        precondition(retina.width == 774 && retina.height == 1340)
        let nominal = try WindowCaptureGeometry(contentRect:phone, pointPixelScale:1)
        precondition(nominal.width == 387 && nominal.height == 670)
        precondition(retina.acceptsFrameScale(2) && !retina.acceptsFrameScale(1))
        precondition(!retina.acceptsFrameScale(.nan))
        let rounded = try WindowCaptureGeometry(contentRect:CGRect(x:0,y:0,width:387.25,height:670.25),pointPixelScale:2)
        precondition(rounded.width == 775 && rounded.height == 1341)
        for scale in [0, -1, Double.infinity, Double.nan, 4.1] {
            do { _ = try WindowCaptureGeometry(contentRect:phone,pointPixelScale:scale); fatalError("invalid scale accepted") }
            catch { }
        }
        for rect in [CGRect.zero, CGRect(x:0,y:0,width:1e20,height:1e20), CGRect(x:0,y:0,width:8000,height:8000)] {
            do { _ = try WindowCaptureGeometry(contentRect:rect,pointPixelScale:2); fatalError("oversized/invalid frame accepted") }
            catch { }
        }
        print("PASS: backing pixels, rounding, scale invalidation and bounded allocation")
    }
}
