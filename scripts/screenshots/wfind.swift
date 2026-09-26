import CoreGraphics
import Foundation
// wfind <minWidth> <minHeight> [layer] — the largest on-screen Mindtalk window matching.
let a = CommandLine.arguments
let minW = Double(a[1])!, minH = Double(a[2])!
let layer = a.count > 3 ? Int(a[3]) : nil
var best: (Int, Double)? = nil
for w in CGWindowListCopyWindowInfo([.optionOnScreenOnly], kCGNullWindowID) as! [[String: Any]]
where (w[kCGWindowOwnerName as String] as? String) == "Mindtalk" {
    if let layer, (w[kCGWindowLayer as String] as? Int) != layer { continue }
    let b = w[kCGWindowBounds as String] as! NSDictionary
    let ww = (b["Width"] as! NSNumber).doubleValue, hh = (b["Height"] as! NSNumber).doubleValue
    guard ww >= minW, hh >= minH else { continue }
    let id = w[kCGWindowNumber as String] as! Int
    if best == nil || ww * hh > best!.1 { best = (id, ww * hh) }
}
if let best { print(best.0) }
