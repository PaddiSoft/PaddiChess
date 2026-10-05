import Foundation

// ICO is a directory of PNG payloads. Raster sizing belongs to sips in build-icons.sh.
// Keeping the generated artwork unchanged prevents platform icons drifting apart.
guard CommandLine.arguments.count >= 3 else {
    fatalError("Usage: MakeIcon.swift output.ico size.png [size.png ...]")
}
let output = URL(fileURLWithPath: CommandLine.arguments[1])
let inputs = try CommandLine.arguments.dropFirst(2).map { path -> (Int, Data) in
    let url = URL(fileURLWithPath: path)
    guard let size = Int(url.deletingPathExtension().lastPathComponent), size > 0, size <= 256 else {
        fatalError("PNG filenames must be their icon size, e.g. 256.png")
    }
    return (size, try Data(contentsOf: url))
}
var data = Data()
func append16(_ value: UInt16) {
    var littleEndian = value.littleEndian
    withUnsafeBytes(of: &littleEndian) { data.append(contentsOf: $0) }
}
func append32(_ value: UInt32) {
    var littleEndian = value.littleEndian
    withUnsafeBytes(of: &littleEndian) { data.append(contentsOf: $0) }
}
append16(0)
append16(1)
append16(UInt16(inputs.count))
var offset = UInt32(6 + 16 * inputs.count)
for (size, png) in inputs {
    let dimension: UInt8 = size == 256 ? 0 : UInt8(size)
    data.append(contentsOf: [dimension, dimension, 0, 0])
    append16(1)
    append16(32)
    append32(UInt32(png.count))
    append32(offset)
    offset += UInt32(png.count)
}
for (_, png) in inputs { data.append(png) }
try data.write(to: output, options: .atomic)
