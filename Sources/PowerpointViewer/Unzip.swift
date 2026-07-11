import Foundation

/// Minimal, dependency-free ZIP extraction for .pptx archives.
///
/// A .pptx file is a standard ZIP container. Rather than pulling in a third-party
/// dependency we shell out to the system `unzip`, which ships with every macOS
/// install, extracting the whole archive into a scratch directory that we own.
enum Unzip {
    struct Error: Swift.Error, LocalizedError {
        let message: String
        var errorDescription: String? { message }
    }

    /// Extracts `archiveURL` into a fresh temporary directory and returns it.
    /// The caller is responsible for removing the directory when finished.
    static func extract(_ archiveURL: URL) throws -> URL {
        let fm = FileManager.default
        let dest = fm.temporaryDirectory
            .appendingPathComponent("PowerpointViewer", isDirectory: true)
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try fm.createDirectory(at: dest, withIntermediateDirectories: true)

        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/unzip")
        // -o overwrite, -qq very quiet, -d destination
        process.arguments = ["-o", "-qq", archiveURL.path, "-d", dest.path]

        let errPipe = Pipe()
        process.standardError = errPipe
        process.standardOutput = Pipe()

        do {
            try process.run()
        } catch {
            throw Error(message: "Could not launch unzip: \(error.localizedDescription)")
        }
        process.waitUntilExit()

        guard process.terminationStatus == 0 else {
            let data = errPipe.fileHandleForReading.readDataToEndOfFile()
            let msg = String(data: data, encoding: .utf8) ?? "unknown error"
            throw Error(message: "This file could not be opened as a PowerPoint archive.\n\(msg)")
        }
        return dest
    }
}
