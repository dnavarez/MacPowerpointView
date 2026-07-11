import Foundation

/// Handles legacy binary PowerPoint (`.ppt`) files.
///
/// A `.ppt` is an OLE2 compound binary document — an entirely different format
/// from `.pptx` (a ZIP of XML). Rather than implement a binary `.ppt` parser,
/// we transparently convert to `.pptx` using LibreOffice when it's installed,
/// and otherwise surface a clear, actionable message.
enum LegacyPPT {
    struct Error: Swift.Error, LocalizedError {
        let message: String
        var errorDescription: String? { message }
    }

    /// OLE2 compound-document signature (D0 CF 11 E0 A1 B1 1A E1).
    private static let oleSignature: [UInt8] = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]

    /// True if the file begins with the OLE2 signature (i.e. a legacy `.ppt`).
    static func isLegacyBinary(_ url: URL) -> Bool {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return false }
        defer { try? handle.close() }
        let head = try? handle.read(upToCount: oleSignature.count)
        return head.map { Array($0) == oleSignature } ?? false
    }

    /// Returns a `.pptx` URL for the given file: the original if it's already a
    /// package, or a freshly converted copy for a legacy `.ppt`.
    static func resolve(_ url: URL) throws -> URL {
        guard isLegacyBinary(url) || url.pathExtension.lowercased() == "ppt" else {
            return url
        }
        guard let soffice = locateSoffice() else {
            throw Error(message: """
            “\(url.lastPathComponent)” is a legacy PowerPoint (.ppt) file, which uses an \
            older binary format this app can't read directly.

            Open it in PowerPoint or Keynote and save a copy as .pptx, then open that here.

            (Tip: install LibreOffice and this app will convert .ppt files automatically.)
            """)
        }
        return try convert(url, using: soffice)
    }

    // MARK: - LibreOffice

    private static func locateSoffice() -> URL? {
        let candidates = [
            "/Applications/LibreOffice.app/Contents/MacOS/soffice",
            "/opt/homebrew/bin/soffice",
            "/usr/local/bin/soffice"
        ]
        return candidates.map { URL(fileURLWithPath: $0) }
            .first { FileManager.default.isExecutableFile(atPath: $0.path) }
    }

    private static func convert(_ url: URL, using soffice: URL) throws -> URL {
        let outDir = FileManager.default.temporaryDirectory
            .appendingPathComponent("SlideViewer-convert", isDirectory: true)
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: outDir, withIntermediateDirectories: true)

        let process = Process()
        process.executableURL = soffice
        process.arguments = ["--headless", "--convert-to", "pptx", "--outdir", outDir.path, url.path]
        let errPipe = Pipe()
        process.standardError = errPipe
        process.standardOutput = Pipe()

        do {
            try process.run()
        } catch {
            throw Error(message: "Could not run LibreOffice to convert the file: \(error.localizedDescription)")
        }
        process.waitUntilExit()

        let converted = outDir.appendingPathComponent(
            url.deletingPathExtension().lastPathComponent + ".pptx")
        guard process.terminationStatus == 0, FileManager.default.fileExists(atPath: converted.path) else {
            let data = errPipe.fileHandleForReading.readDataToEndOfFile()
            let detail = String(data: data, encoding: .utf8).flatMap {
                $0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? nil : $0
            } ?? "conversion did not produce a .pptx file"
            throw Error(message: "Converting “\(url.lastPathComponent)” from .ppt failed.\n\(detail)")
        }
        return converted
    }
}
