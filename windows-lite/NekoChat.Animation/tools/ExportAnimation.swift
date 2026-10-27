import AppKit

@main enum ExportAnimation {
    static func render(_ view: DesktopPetView) -> Data {
        let bitmap = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 416, pixelsHigh: 384,
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        bitmap.size = view.bounds.size
        bitmap.bitmapData!.initialize(repeating: 0, count: bitmap.bytesPerRow * bitmap.pixelsHigh)
        view.cacheDisplay(in: view.bounds, to: bitmap)
        return bitmap.representation(using: .png, properties: [:])!
    }
    static func main() throws {
        _ = NSApplication.shared
        let source = URL(fileURLWithPath: CommandLine.arguments[1])
        let output = URL(fileURLWithPath: CommandLine.arguments[2])
        let resources = source.appendingPathComponent("Resources/DesktopPet")
        let fm = FileManager.default
        try fm.createDirectory(at: output, withIntermediateDirectories: true)
        let clips: [(String, DesktopPetPose, Double, Int, Bool, Bool)] = [
            ("standing", .standing, 1, 1, true, false),
            ("walk-left", .walking, Double(DesktopPetView.strideDistance) / 70, 16, true, true),
            ("walk-right", .walking, Double(DesktopPetView.strideDistance) / 70, 16, true, false),
            ("walk-left-start", .walking, Double(DesktopPetView.strideDistance) / 70, 16, false, true),
            ("walk-right-start", .walking, Double(DesktopPetView.strideDistance) / 70, 16, false, false),
            ("curious", .curious, 5.6, 56, true, false),
            ("grooming", .grooming, 3.2, 64, false, false),
            ("yawning", .yawning, 3.2, 48, false, false),
            ("loafing", .loafing, 5.6, 56, true, false),
            ("sleeping", .sleeping, 5.6, 56, true, false),
            ("petted", .petted, 1.6, 24, false, false)
        ]
        var definitions: [String: Any] = [:]
        for (name, _, duration, count, loop, _) in clips {
            definitions[name] = ["duration": duration, "count": count, "loop": loop]
        }
        var width = 0, height = 0
        for id in CatAppearance.presetIDs {
            try autoreleasepool {
                let view = DesktopPetView(resources: resources, appearance: CatAppearance.makePreset(id)!)
                guard view.loadedPickupFrames == 36, view.loadedAssets.count == 14 else {
                    throw NSError(domain: "MissingNativeArtwork", code: 1)
                }
                let folder = output.appendingPathComponent(id)
                try fm.createDirectory(at: folder, withIntermediateDirectories: true)
                for index in Set(DesktopPetLift.sourceImageIndices) {
                    view.pose = .carried
                    view.lift = DesktopPetLift(spriteFrame: DesktopPetLift.framePositions[DesktopPetLift.sourceImageIndices.firstIndex(of: index)!])
                    let data = id == "original"
                        ? try Data(contentsOf: resources.appendingPathComponent(String(format: "Pickup/%02d.png", index)))
                        : render(view)
                    let bitmap = NSBitmapImageRep(data: data)!
                    width = bitmap.pixelsWide; height = bitmap.pixelsHigh
                    try data.write(to: folder.appendingPathComponent(String(format: "lift-%02d.png", index)))
                }
                for (name, pose, duration, count, loop, left) in clips {
                    for index in 0..<count {
                        try autoreleasepool {
                            let phase = duration * Double(index) / Double(loop ? count : max(1, count - 1))
                            // A full gait cycle starts after native startup easing has completed.
                            let distance = pose == .walking ? DesktopPetView.strideDistance * ((name.hasSuffix("-start") ? 0 : 1) + CGFloat(index) / CGFloat(count)) : 0
                            view.pose = pose; view.phase = phase; view.facingLeft = left; view.walkDistance = distance
                            let data = render(view)
                            try data.write(to: folder.appendingPathComponent(String(format: "%@-%03d.png", name, index)))
                        }
                    }
                }
                print("Exported \(id)"); fflush(stdout)
            }
        }
        // Native reference vectors exercise forward, reverse and interrupted travel.
        var vectors: [[String: Any]] = []
        for start in DesktopPetLift.framePositions {
            for end in [CGFloat(0), DesktopPetLift.carryFrame, DesktopPetLift.lastFrame] {
                for elapsed in [0.0, 0.016, 0.049, 0.05, 0.1, 0.2, 0.4, 0.85, 2] {
                    let position = DesktopPetLift.travel(from: start, to: end, elapsed: elapsed)
                    vectors.append(["start": DesktopPetLift.imageIndex(for: start), "end": DesktopPetLift.imageIndex(for: end),
                                    "elapsed": elapsed, "expected": DesktopPetLift.imageIndex(for: position)])
                }
            }
        }
        let manifest: [String: Any] = [
            "version": 1, "sourceRepo": "yuipan0611/whats-up-my-neighbor-ai-input-macos",
            "sourceCommit": "f2e3e49577cccc431e190d4853a8c43cd6c89772",
            "logicalWidth": 208, "logicalHeight": 192, "pixelWidth": width, "pixelHeight": height,
            "coats": CatAppearance.presetIDs, "coatNames": CatAppearance.presetNames,
            "times": DesktopPetLift.frameTimes, "sources": DesktopPetLift.sourceImageIndices,
            "carryIndex": DesktopPetLift.imageIndex(for: DesktopPetLift.carryFrame),
            "pickupDuration": DesktopPetLift.pickupDuration, "landingDuration": DesktopPetLift.landingDuration,
            "clips": definitions, "travelVectors": vectors
        ]
        try JSONSerialization.data(withJSONObject: manifest, options: [.prettyPrinted, .sortedKeys])
            .write(to: output.appendingPathComponent("manifest.json"))
    }
}
