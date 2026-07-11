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

        // Some producers store `_rels` and other directories with permissions
        // that omit the owner execute bit (e.g. drw-rw-r--), which makes the
        // extracted directory non-traversable so the parser can't read the parts
        // inside. Normalize permissions on the scratch tree we own: every
        // directory readable+traversable, every file readable.
        normalizePermissions(at: dest, fileManager: fm)

        return dest
    }

    /// Grants the owner rwx on all directories and rw on all files under `url`.
    ///
    /// Each directory is made traversable *before* its contents are listed —
    /// a directory stored without the execute bit can't be descended into
    /// otherwise, so a plain enumerator would never see the files inside it.
    private static func normalizePermissions(at url: URL, fileManager fm: FileManager) {
        var isDir: ObjCBool = false
        guard fm.fileExists(atPath: url.path, isDirectory: &isDir) else { return }

        if isDir.boolValue {
            try? fm.setAttributes([.posixPermissions: NSNumber(value: 0o755)], ofItemAtPath: url.path)
            let children = (try? fm.contentsOfDirectory(at: url, includingPropertiesForKeys: nil)) ?? []
            for child in children {
                normalizePermissions(at: child, fileManager: fm)
            }
        } else {
            try? fm.setAttributes([.posixPermissions: NSNumber(value: 0o644)], ofItemAtPath: url.path)
        }
    }
}
